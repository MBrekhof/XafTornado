using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using LlmTornado.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace XafTornado.Module.Services
{
    /// <summary>
    /// Creates generic <see cref="AIFunction"/> tools that work with any entity
    /// discovered by <see cref="SchemaDiscoveryService"/>.
    /// Pattern: <c>[Description]</c> on method + params, <c>AIFunctionFactory.Create(method, name)</c>.
    /// </summary>
    public sealed class AIToolsProvider
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly SchemaDiscoveryService _schemaService;
        private readonly ILogger<AIToolsProvider> _logger;
        private readonly INavigationService _navigationService;
        private readonly ActiveViewContext _activeViewContext;
        private List<AIFunction> _tools;

        /// <summary>
        /// When set (WinForms), ObjectSpaces are created via <c>Application.CreateObjectSpace</c>
        /// on the UI thread, bypassing <c>INonSecuredObjectSpaceFactory</c> which doesn't work
        /// from manually-created DI scopes in WinForms.
        /// Blazor does not need this because <c>AsyncLocal</c> carries the context automatically.
        /// </summary>
        public XafApplication Application { get; set; }

        /// <summary>
        /// The WinForms UI <see cref="SynchronizationContext"/> for dispatching ObjectSpace creation.
        /// </summary>
        public SynchronizationContext UiContext { get; set; }

        public AIToolsProvider(IServiceProvider serviceProvider, SchemaDiscoveryService schemaService,
            INavigationService navigationService = null, ActiveViewContext activeViewContext = null)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _schemaService = schemaService ?? throw new ArgumentNullException(nameof(schemaService));
            _logger = serviceProvider.GetRequiredService<ILogger<AIToolsProvider>>();
            _navigationService = navigationService;
            _activeViewContext = activeViewContext;
        }

        public IReadOnlyList<AIFunction> Tools => _tools ??= CreateTools();

        private List<AIFunction> CreateTools()
        {
            var tools = new List<AIFunction>
            {
                AIFunctionFactory.Create(ListEntities, "list_entities"),
                AIFunctionFactory.Create(DescribeEntity, "describe_entity"),
                AIFunctionFactory.Create(QueryEntity, "query_entity"),
                AIFunctionFactory.Create(CreateEntity, "create_entity"),
            };

            if (_navigationService != null)
            {
                tools.Add(AIFunctionFactory.Create(NavigateToList, "navigate_to_list"));
                tools.Add(AIFunctionFactory.Create(NavigateToDetail, "navigate_to_detail"));
                tools.Add(AIFunctionFactory.Create(FilterActiveList, "filter_active_list"));
                tools.Add(AIFunctionFactory.Create(ClearActiveListFilter, "clear_active_list_filter"));
                tools.Add(AIFunctionFactory.Create(SaveActiveView, "save_active_view"));
                tools.Add(AIFunctionFactory.Create(CloseActiveView, "close_active_view"));
            }

            if (_activeViewContext != null)
            {
                tools.Add(AIFunctionFactory.Create(GetActiveView, "get_active_view"));
                tools.Add(AIFunctionFactory.Create(UpdateEntity, "update_entity"));
            }

            return tools;
        }

        /// <summary>
        /// Converts AIFunction definitions to LLMTornado Tool format.
        /// AIFunction instances are kept for execution; Tool instances are sent to the LLM.
        /// </summary>
        public IReadOnlyList<Tool> GetTornadoTools()
        {
            var tornadoTools = new List<Tool>();

            foreach (var fn in Tools)
            {
                // AIFunction.JsonSchema is a JsonElement containing the parameters schema.
                // ToolFunction accepts a JsonElement for the parameters schema directly.
                var toolFunction = new ToolFunction(fn.Name, fn.Description, fn.JsonSchema);
                tornadoTools.Add(new Tool(toolFunction));
            }

            return tornadoTools;
        }

        // -- Helpers ---------------------------------------------------------------

        /// <summary>
        /// Creates a DI scope + non-secured object space for the given entity type.
        /// Callers MUST dispose the returned <see cref="ScopedObjectSpace"/>
        /// which disposes both the object space and the scope.
        /// </summary>
        private ScopedObjectSpace GetObjectSpace(Type entityType)
        {
            // WinForms: INonSecuredObjectSpaceFactory doesn't work from manually-created
            // DI scopes. Use XafApplication.CreateObjectSpace directly on the UI thread.
            if (Application != null)
            {
                IObjectSpace os = null;
                if (UiContext != null && SynchronizationContext.Current != UiContext)
                {
                    Exception caught = null;
                    UiContext.Send(_ =>
                    {
                        try { os = Application.CreateObjectSpace(entityType); }
                        catch (Exception ex) { caught = ex; }
                    }, null);
                    if (caught != null)
                        throw caught;
                }
                else
                {
                    os = Application.CreateObjectSpace(entityType);
                }
                return new ScopedObjectSpace(os, null);
            }

            // Blazor: DI scope + INonSecuredObjectSpaceFactory (AsyncLocal carries context).
            var scope = _serviceProvider.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<INonSecuredObjectSpaceFactory>();
            var os2 = factory.CreateNonSecuredObjectSpace(entityType);
            return new ScopedObjectSpace(os2, scope);
        }

        /// <summary>Wraps an IObjectSpace + IServiceScope for joint disposal.</summary>
        private sealed class ScopedObjectSpace : IDisposable
        {
            public IObjectSpace Os { get; }
            private readonly IServiceScope _scope;

            public ScopedObjectSpace(IObjectSpace os, IServiceScope scope)
            {
                Os = os;
                _scope = scope;
            }

            public void Dispose()
            {
                Os.Dispose();
                _scope?.Dispose();
            }
        }

        /// <summary>
        /// Returns a comma-separated list of all known entity names.
        /// </summary>
        private string GetEntityNameList() =>
            string.Join(", ", _schemaService.Schema.Entities.Select(e => e.Name));

        /// <summary>
        /// Formats a single entity object as a line of "Property: Value" pairs
        /// using XAF <see cref="ITypeInfo"/> metadata.
        /// </summary>
        private string FormatObject(object obj, EntityInfo entityInfo, ITypeInfo typeInfo)
        {
            var parts = new List<string>();
            foreach (var prop in entityInfo.Properties)
            {
                var member = typeInfo.FindMember(prop.Name);
                if (member == null) continue;
                var val = member.GetValue(obj);
                parts.Add($"{prop.Name}: {FormatValue(val)}");
            }
            // Include to-one relationship references (show a summary, not the whole object)
            foreach (var rel in entityInfo.Relationships.Where(r => !r.IsCollection))
            {
                var member = typeInfo.FindMember(rel.PropertyName);
                if (member == null) continue;
                var refObj = member.GetValue(obj);
                if (refObj != null)
                    parts.Add($"{rel.PropertyName}: {GetObjectDisplayText(refObj)}");
            }
            return string.Join(" | ", parts);
        }

        /// <summary>
        /// Attempts to produce a human-readable label for an entity object
        /// by looking for common "name" properties.
        /// </summary>
        private static string GetObjectDisplayText(object obj)
        {
            if (obj == null) return "null";
            var type = obj.GetType();
            // Try common name properties
            foreach (var propName in new[] { "Name", "CompanyName", "Title", "FullName", "FirstName", "Description", "InvoiceNumber" })
            {
                var prop = type.GetProperty(propName);
                if (prop != null)
                {
                    var val = prop.GetValue(obj);
                    if (val != null) return val.ToString();
                }
            }
            return obj.ToString();
        }

        private static string FormatValue(object val)
        {
            if (val == null) return "N/A";
            if (val is DateTime dt) return dt.ToString("yyyy-MM-dd");
            if (val is decimal d) return d.ToString("F2");
            if (val is double dbl) return dbl.ToString("F2");
            if (val is float f) return f.ToString("F2");
            return val.ToString();
        }

        /// <summary>
        /// Parses "Key=Value;Key2=Value2" into a list of key-value pairs.
        /// </summary>
        private static List<(string Key, string Value)> ParsePairs(string input)
        {
            var pairs = new List<(string, string)>();
            if (string.IsNullOrWhiteSpace(input)) return pairs;
            foreach (var segment in input.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIndex = segment.IndexOf('=');
                if (eqIndex <= 0) continue;
                var key = segment.Substring(0, eqIndex).Trim();
                var value = segment.Substring(eqIndex + 1).Trim();
                if (!string.IsNullOrEmpty(key))
                    pairs.Add((key, value));
            }
            return pairs;
        }

        /// <summary>
        /// Converts a string value to the target CLR type, handling enums, dates,
        /// numbers, booleans, and nullable wrappers.
        /// </summary>
        private static object ConvertValue(string value, Type targetType)
        {
            if (value == null) return null;

            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
            {
                if (string.IsNullOrWhiteSpace(value)) return null;
                return ConvertValue(value, underlying);
            }

            if (targetType == typeof(string)) return value;
            if (targetType.IsEnum) return Enum.Parse(targetType, value, ignoreCase: true);
            if (targetType == typeof(DateTime)) return DateTime.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(long)) return long.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(decimal)) return decimal.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(double)) return double.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(float)) return float.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return bool.Parse(value);
            if (targetType == typeof(Guid)) return Guid.Parse(value);

            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }

        // -- Tool implementations --------------------------------------------------

        [Description("List all available entities (tables) in the database with their properties and relationships.")]
        private string ListEntities()
        {
            _logger.LogInformation("[Tool:list_entities] Called");
            try
            {
                var schema = _schemaService.Schema;
                var sb = new StringBuilder();
                sb.AppendLine("Available entities:");

                foreach (var entity in schema.Entities)
                {
                    var props = string.Join(", ", entity.Properties.Select(p => p.Name));
                    sb.Append($"- {entity.Name} ({props})");

                    var rels = entity.Relationships;
                    if (rels.Count > 0)
                    {
                        var relDescriptions = rels.Select(r =>
                            r.IsCollection ? $"has many {r.TargetEntity}" : $"belongs to {r.TargetEntity}");
                        sb.Append($" -> {string.Join(", ", relDescriptions)}");
                    }
                    sb.AppendLine();

                    // Enum values for properties
                    foreach (var p in entity.Properties.Where(p => p.EnumValues.Count > 0))
                        sb.AppendLine($"  - {p.Name} values: {string.Join(", ", p.EnumValues)}");
                }

                var result = sb.ToString();
                _logger.LogInformation("[Tool:list_entities] Returning {Len} chars", result.Length);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:list_entities] Error");
                return $"Error listing entities: {ex.Message}";
            }
        }

        [Description("Get full schema details for a single entity — properties, types, relationships, and enum values. Call this before querying or creating records of an unfamiliar entity.")]
        private string DescribeEntity(
            [Description("Entity name to describe (e.g. 'Customer', 'Order'). Use list_entities to see available names.")] string entityName)
        {
            _logger.LogInformation("[Tool:describe_entity] Called with entity={Entity}", entityName);
            try
            {
                if (string.IsNullOrWhiteSpace(entityName))
                    return $"Entity name is required. Available entities: {GetEntityNameList()}";

                var entityInfo = _schemaService.Schema.FindEntity(entityName);
                if (entityInfo == null)
                    return $"Entity '{entityName}' not found. Available entities: {GetEntityNameList()}";

                var sb = new StringBuilder();
                sb.AppendLine($"**{entityInfo.Name}**");
                if (!string.IsNullOrEmpty(entityInfo.Description))
                    sb.AppendLine(entityInfo.Description);
                sb.AppendLine();

                // Properties
                sb.AppendLine("Properties:");
                foreach (var prop in entityInfo.Properties)
                {
                    var required = prop.IsRequired ? " (required)" : "";
                    var desc = !string.IsNullOrEmpty(prop.Description) ? $" — {prop.Description}" : "";
                    sb.AppendLine($"  - {prop.Name}: {prop.TypeName}{required}{desc}");

                    if (prop.EnumValues.Count > 0)
                        sb.AppendLine($"    Values: {string.Join(", ", prop.EnumValues)}");
                }

                // Relationships
                if (entityInfo.Relationships.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Relationships:");
                    foreach (var rel in entityInfo.Relationships)
                    {
                        var kind = rel.IsCollection ? "has many" : "belongs to";
                        sb.AppendLine($"  - {rel.PropertyName}: {kind} {rel.TargetEntity}");
                    }
                }

                var result = sb.ToString();
                _logger.LogInformation("[Tool:describe_entity] Returning {Len} chars for {Entity}", result.Length, entityName);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:describe_entity] Error");
                return $"Error describing {entityName}: {ex.Message}";
            }
        }

        [Description("Query records of any entity (table) in the database. Call describe_entity first if you are unsure about property names or types.")]
        private string QueryEntity(
            [Description("Entity name to query (e.g. 'Customer', 'Order', 'Product'). Use list_entities to see available names.")] string entityName,
            [Description("Optional filter as semicolon-separated 'PropertyName=value' pairs. Example: 'Status=New;Country=USA'. Omit for no filter.")] string filter = "",
            [Description("Maximum number of records to return. Default is 25.")] int top = 25)
        {
            _logger.LogInformation("[Tool:query_entity] Called with entity={Entity}, filter={Filter}, top={Top}", entityName, filter, top);
            try
            {
                if (string.IsNullOrWhiteSpace(entityName))
                    return $"Entity name is required. Available entities: {GetEntityNameList()}";

                var entityInfo = _schemaService.Schema.FindEntity(entityName);
                if (entityInfo == null)
                    return $"Entity '{entityName}' not found. Available entities: {GetEntityNameList()}";

                var entityType = entityInfo.ClrType;
                if (top <= 0) top = 25;

                using var sos = GetObjectSpace(entityType);
                var os = sos.Os;
                var typeInfo = XafTypesInfo.Instance.FindTypeInfo(entityType);

                // Retrieve all objects of this type
                var allObjects = os.GetObjects(entityType);
                IEnumerable<object> results = allObjects.Cast<object>();

                // Apply in-memory filters
                var filterPairs = ParsePairs(filter);
                foreach (var (key, value) in filterPairs)
                {
                    // Try scalar property first
                    var propInfo = entityInfo.Properties
                        .FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (propInfo != null)
                    {
                        var member = typeInfo.FindMember(propInfo.Name);
                        if (member != null)
                        {
                            // For string properties, use Contains (case-insensitive)
                            if (propInfo.ClrType == typeof(string))
                            {
                                results = results.Where(o =>
                                {
                                    var v = member.GetValue(o) as string;
                                    return v != null && v.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
                                });
                            }
                            else
                            {
                                try
                                {
                                    var converted = ConvertValue(value, propInfo.ClrType);
                                    results = results.Where(o => Equals(member.GetValue(o), converted));
                                }
                                catch
                                {
                                    return $"Cannot convert filter value '{value}' to type '{propInfo.TypeName}' for property '{key}'.";
                                }
                            }
                        }
                        continue;
                    }

                    // Try relationship (to-one navigation) — match by display text
                    var relInfo = entityInfo.Relationships
                        .FirstOrDefault(r => !r.IsCollection && r.PropertyName.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (relInfo != null)
                    {
                        var member = typeInfo.FindMember(relInfo.PropertyName);
                        if (member != null)
                        {
                            results = results.Where(o =>
                            {
                                var refObj = member.GetValue(o);
                                if (refObj == null) return false;
                                var display = GetObjectDisplayText(refObj);
                                return display.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
                            });
                        }
                        continue;
                    }

                    var availableProps = string.Join(", ", entityInfo.Properties.Select(p => p.Name)
                        .Concat(entityInfo.Relationships.Where(r => !r.IsCollection).Select(r => r.PropertyName)));
                    return $"Property '{key}' not found on {entityInfo.Name}. Available: {availableProps}";
                }

                var list = results.Take(top).ToList();
                if (list.Count == 0) return $"No {entityInfo.Name} records found matching the given criteria.";

                var sb = new StringBuilder();
                sb.AppendLine($"Found {list.Count} {entityInfo.Name} record(s):");
                foreach (var obj in list)
                    sb.AppendLine(FormatObject(obj, entityInfo, typeInfo));

                var result = sb.ToString();
                _logger.LogInformation("[Tool:query_entity] Returning {Len} chars, {Count} records", result.Length, list.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:query_entity] Error");
                return $"Error querying {entityName}: {ex.Message}";
            }
        }

        [Description("Create a new record of any entity in the database. Call describe_entity first to see required fields, property types, and relationships.")]
        private string CreateEntity(
            [Description("Entity name to create (e.g. 'Customer', 'Order', 'Product'). Use list_entities to see available names.")] string entityName,
            [Description("Semicolon-separated 'PropertyName=value' pairs. For reference properties (relationships), provide a search term to match by name. Example: 'CompanyName=Acme Corp;Country=USA' or 'Customer=Acme;Status=New'.")] string properties)
        {
            _logger.LogInformation("[Tool:create_entity] Called with entity={Entity}, properties={Props}", entityName, properties);
            try
            {
                if (string.IsNullOrWhiteSpace(entityName))
                    return $"Entity name is required. Available entities: {GetEntityNameList()}";

                var entityInfo = _schemaService.Schema.FindEntity(entityName);
                if (entityInfo == null)
                    return $"Entity '{entityName}' not found. Available entities: {GetEntityNameList()}";

                if (string.IsNullOrWhiteSpace(properties))
                {
                    var availableProps = string.Join(", ", entityInfo.Properties.Select(p => p.Name));
                    var availableRels = string.Join(", ", entityInfo.Relationships.Where(r => !r.IsCollection).Select(r => r.PropertyName));
                    return $"Properties are required. {entityInfo.Name} properties: {availableProps}" +
                           (string.IsNullOrEmpty(availableRels) ? "" : $". Relationships: {availableRels}");
                }

                var entityType = entityInfo.ClrType;
                using var sos = GetObjectSpace(entityType);
                var os = sos.Os;
                var typeInfo = XafTypesInfo.Instance.FindTypeInfo(entityType);

                var obj = os.CreateObject(entityType);
                var pairs = ParsePairs(properties);
                var setProperties = new List<string>();

                foreach (var (key, value) in pairs)
                {
                    // Check if it's a scalar property
                    var propInfo = entityInfo.Properties
                        .FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (propInfo != null)
                    {
                        var member = typeInfo.FindMember(propInfo.Name);
                        if (member != null)
                        {
                            try
                            {
                                var converted = ConvertValue(value, propInfo.ClrType);
                                member.SetValue(obj, converted);
                                setProperties.Add($"{propInfo.Name}: {FormatValue(converted)}");
                            }
                            catch (Exception ex)
                            {
                                return $"Error setting {propInfo.Name}: cannot convert '{value}' to {propInfo.TypeName}. {ex.Message}";
                            }
                        }
                        continue;
                    }

                    // Check if it's a to-one relationship
                    var relInfo = entityInfo.Relationships
                        .FirstOrDefault(r => !r.IsCollection && r.PropertyName.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (relInfo != null)
                    {
                        // Look up the referenced entity by searching for a natural key match
                        var refObjects = os.GetObjects(relInfo.TargetClrType);
                        object matched = null;
                        foreach (var refObj in refObjects)
                        {
                            var display = GetObjectDisplayText(refObj);
                            if (display.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                matched = refObj;
                                break;
                            }
                        }

                        if (matched == null)
                        {
                            // List some available values to help the user
                            var available = refObjects.Cast<object>()
                                .Take(10)
                                .Select(GetObjectDisplayText);
                            return $"{relInfo.PropertyName} '{value}' not found. Available {relInfo.TargetEntity} records: {string.Join(", ", available)}";
                        }

                        var member = typeInfo.FindMember(relInfo.PropertyName);
                        if (member != null)
                        {
                            member.SetValue(obj, matched);
                            setProperties.Add($"{relInfo.PropertyName}: {GetObjectDisplayText(matched)}");
                        }
                        continue;
                    }

                    // Property not found
                    var allProps = string.Join(", ", entityInfo.Properties.Select(p => p.Name)
                        .Concat(entityInfo.Relationships.Where(r => !r.IsCollection).Select(r => r.PropertyName)));
                    return $"Property '{key}' not found on {entityInfo.Name}. Available: {allProps}";
                }

                os.CommitChanges();
                _navigationService?.RefreshActiveView();

                var summary = string.Join(" | ", setProperties);
                var result = $"{entityInfo.Name} created successfully! {summary}";
                _logger.LogInformation("[Tool:create_entity] {Result}", result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:create_entity] Error");
                return $"Error creating {entityName}: {ex.Message}";
            }
        }

        // -- Navigation tools ------------------------------------------------------

        [Description("Navigate the user's application to the list view showing all records of an entity. Use this when the user wants to see or browse data in the app.")]
        private string NavigateToList(
            [Description("Entity name to navigate to (e.g. 'Customer', 'Order'). Use list_entities to see available names.")] string entityName)
        {
            _logger.LogInformation("[Tool:navigate_to_list] Called with entity={Entity}", entityName);
            try
            {
                if (string.IsNullOrWhiteSpace(entityName))
                    return $"Entity name is required. Available entities: {GetEntityNameList()}";

                var entityInfo = _schemaService.Schema.FindEntity(entityName);
                if (entityInfo == null)
                    return $"Entity '{entityName}' not found. Available entities: {GetEntityNameList()}";

                _navigationService.NavigateToListView(entityName);
                return $"Navigating to {entityInfo.Name} list view.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:navigate_to_list] Error");
                return $"Error navigating to {entityName}: {ex.Message}";
            }
        }

        [Description("Navigate the user's application to a specific record's detail view. Use this when the user wants to open or view a particular record.")]
        private string NavigateToDetail(
            [Description("Entity name (e.g. 'Customer', 'Order'). Use list_entities to see available names.")] string entityName,
            [Description("The record identifier — either a primary key (GUID) or a search term to match by name.")] string identifier)
        {
            _logger.LogInformation("[Tool:navigate_to_detail] Called with entity={Entity}, id={Id}", entityName, identifier);
            try
            {
                if (string.IsNullOrWhiteSpace(entityName))
                    return $"Entity name is required. Available entities: {GetEntityNameList()}";

                var entityInfo = _schemaService.Schema.FindEntity(entityName);
                if (entityInfo == null)
                    return $"Entity '{entityName}' not found. Available entities: {GetEntityNameList()}";

                if (string.IsNullOrWhiteSpace(identifier))
                    return $"An identifier (key or search term) is required to find the record.";

                _navigationService.NavigateToDetailView(entityName, identifier);
                return $"Navigating to {entityInfo.Name} record matching '{identifier}'.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:navigate_to_detail] Error");
                return $"Error navigating to {entityName} detail: {ex.Message}";
            }
        }

        // -- Active view tools ---------------------------------------------------------

        [Description("Get information about what the user is currently viewing in the application. Returns the entity name, view type (list or detail), view ID, and for detail views the specific record being viewed. Always call this first when the user refers to 'this record', 'the current view', 'this list', etc.")]
        private string GetActiveView()
        {
            _logger.LogInformation("[Tool:get_active_view] Called");
            try
            {
                if (_activeViewContext == null || _activeViewContext.EntityName == null)
                    return "No active view context available.";

                var entityInfo = _schemaService.Schema.FindEntity(_activeViewContext.EntityName);
                var sb = new StringBuilder();
                sb.AppendLine($"The user is currently viewing: **{_activeViewContext.EntityName}** ({(_activeViewContext.IsListView ? "List View" : "Detail View")})");
                sb.AppendLine($"View ID: {_activeViewContext.ViewId}");

                if (!_activeViewContext.IsListView && _activeViewContext.CurrentObjectDisplay != null)
                {
                    sb.AppendLine($"Current record: **{_activeViewContext.CurrentObjectDisplay}** (key: {_activeViewContext.CurrentObjectKey})");

                    // Show the record's current field values so the AI knows what can be changed
                    if (entityInfo != null && _activeViewContext.CurrentObjectKey != null)
                    {
                        try
                        {
                            using var sos = GetObjectSpace(entityInfo.ClrType);
                            var typeInfo = XafTypesInfo.Instance.FindTypeInfo(entityInfo.ClrType);
                            var key = ConvertValue(_activeViewContext.CurrentObjectKey, typeInfo.KeyMember.MemberType);
                            var obj = sos.Os.GetObjectByKey(entityInfo.ClrType, key);
                            if (obj != null)
                            {
                                sb.AppendLine($"Fields: {FormatObject(obj, entityInfo, typeInfo)}");
                            }
                        }
                        catch
                        {
                            // Best effort — don't fail the tool if we can't load the record
                        }
                    }
                }

                if (entityInfo != null && _activeViewContext.IsListView)
                {
                    var props = string.Join(", ", entityInfo.Properties.Select(p => p.Name));
                    sb.AppendLine($"Available properties for filtering: {props}");
                    var rels = entityInfo.Relationships.Where(r => !r.IsCollection).Select(r => r.PropertyName);
                    if (rels.Any())
                        sb.AppendLine($"Relationships (can filter by): {string.Join(", ", rels)}");
                }

                if (entityInfo != null && !_activeViewContext.IsListView)
                {
                    var props = string.Join(", ", entityInfo.Properties.Select(p => p.Name));
                    sb.AppendLine($"Editable properties: {props}");
                    sb.AppendLine("Use update_entity to modify this record's fields.");
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:get_active_view] Error");
                return $"Error getting active view: {ex.Message}";
            }
        }

        [Description("Filter the currently active list view using DevExpress criteria syntax. Use get_active_view first to know what entity is displayed. Common patterns: [PropertyName] = 'value', Contains([PropertyName], 'text'), [Category.Name] = 'Grains', [Price] > 10.")]
        private string FilterActiveList(
            [Description("DevExpress criteria expression. Examples: \"[Category.Name] = 'Grains'\", \"Contains([CompanyName], 'market')\", \"[UnitPrice] > 20\", \"[Status] = 'Active' And [Country] = 'USA'\"")] string criteria)
        {
            _logger.LogInformation("[Tool:filter_active_list] Called with criteria={Criteria}", criteria);
            try
            {
                if (_activeViewContext == null || !_activeViewContext.IsListView)
                    return "No active list view to filter. Use navigate_to_list first to open a list view.";

                if (string.IsNullOrWhiteSpace(criteria))
                    return "A criteria expression is required. Example: [Category.Name] = 'Grains'";

                _navigationService.FilterActiveList(criteria);
                return $"Filter applied to {_activeViewContext.EntityName} list: {criteria}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:filter_active_list] Error");
                return $"Error filtering list: {ex.Message}";
            }
        }

        [Description("Remove the AI-applied filter from the currently active list view, showing all records again.")]
        private string ClearActiveListFilter()
        {
            _logger.LogInformation("[Tool:clear_active_list_filter] Called");
            try
            {
                if (_activeViewContext == null || !_activeViewContext.IsListView)
                    return "No active list view to clear filter from.";

                _navigationService.ClearActiveListFilter();
                return $"Filter cleared from {_activeViewContext.EntityName} list. All records are now visible.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:clear_active_list_filter] Error");
                return $"Error clearing filter: {ex.Message}";
            }
        }

        // -- Save / Close tools --------------------------------------------------------

        [Description("Save (commit) changes in the currently active detail view. Use this when the user says 'save', 'save this', 'save changes', etc.")]
        private string SaveActiveView()
        {
            _logger.LogInformation("[Tool:save_active_view] Called");
            try
            {
                _navigationService.SaveActiveView();
                return "Changes saved successfully.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:save_active_view] Error");
                return $"Error saving: {ex.Message}";
            }
        }

        [Description("Close the currently active view and return to the previous view. Use this when the user says 'close', 'go back', 'close this view', etc.")]
        private string CloseActiveView()
        {
            _logger.LogInformation("[Tool:close_active_view] Called");
            try
            {
                _navigationService.CloseActiveView();
                return "View closed.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:close_active_view] Error");
                return $"Error closing view: {ex.Message}";
            }
        }

        // -- Update tool ---------------------------------------------------------------

        [Description("Update (modify) an existing record in the database. Use get_active_view first to find the current record's key when the user says 'this record' or 'change this'. You can also update any record by providing its entity name and identifier.")]
        private string UpdateEntity(
            [Description("Entity name (e.g. 'Customer', 'Supplier', 'Product'). Use list_entities to see available names.")] string entityName,
            [Description("The record identifier — either the primary key (GUID) or a search term to match by name. Use get_active_view to get the key of the currently viewed record.")] string identifier,
            [Description("Semicolon-separated 'PropertyName=value' pairs for fields to update. Example: 'ContactName=Just Testing;Country=Netherlands'. For reference properties, provide a search term to match by name.")] string properties)
        {
            _logger.LogInformation("[Tool:update_entity] Called with entity={Entity}, id={Id}, properties={Props}", entityName, identifier, properties);
            try
            {
                if (string.IsNullOrWhiteSpace(entityName))
                    return $"Entity name is required. Available entities: {GetEntityNameList()}";

                var entityInfo = _schemaService.Schema.FindEntity(entityName);
                if (entityInfo == null)
                    return $"Entity '{entityName}' not found. Available entities: {GetEntityNameList()}";

                if (string.IsNullOrWhiteSpace(identifier))
                    return "An identifier (key or search term) is required. Use get_active_view to get the key of the current record.";

                if (string.IsNullOrWhiteSpace(properties))
                {
                    var availableProps = string.Join(", ", entityInfo.Properties.Select(p => p.Name));
                    return $"Properties to update are required. {entityInfo.Name} properties: {availableProps}";
                }

                var entityType = entityInfo.ClrType;
                using var sos = GetObjectSpace(entityType);
                var os = sos.Os;
                var typeInfo = XafTypesInfo.Instance.FindTypeInfo(entityType);

                // Try to find the object by primary key first, then by display text search
                object obj = null;
                try
                {
                    var key = ConvertValue(identifier, typeInfo.KeyMember.MemberType);
                    obj = os.GetObjectByKey(entityType, key);
                }
                catch
                {
                    // Not a valid key format — fall through to search
                }

                if (obj == null)
                {
                    // Search by display text
                    var allObjects = os.GetObjects(entityType);
                    foreach (var candidate in allObjects)
                    {
                        var display = GetObjectDisplayText(candidate);
                        if (display != null && display.IndexOf(identifier, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            obj = candidate;
                            break;
                        }
                    }
                }

                if (obj == null)
                    return $"No {entityInfo.Name} record found matching '{identifier}'.";

                var pairs = ParsePairs(properties);
                var updatedProperties = new List<string>();

                foreach (var (key, value) in pairs)
                {
                    // Check scalar property
                    var propInfo = entityInfo.Properties
                        .FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (propInfo != null)
                    {
                        var member = typeInfo.FindMember(propInfo.Name);
                        if (member != null)
                        {
                            try
                            {
                                var oldVal = member.GetValue(obj);
                                var converted = ConvertValue(value, propInfo.ClrType);
                                member.SetValue(obj, converted);
                                updatedProperties.Add($"{propInfo.Name}: {FormatValue(oldVal)} → {FormatValue(converted)}");
                            }
                            catch (Exception ex)
                            {
                                return $"Error setting {propInfo.Name}: cannot convert '{value}' to {propInfo.TypeName}. {ex.Message}";
                            }
                        }
                        continue;
                    }

                    // Check to-one relationship
                    var relInfo = entityInfo.Relationships
                        .FirstOrDefault(r => !r.IsCollection && r.PropertyName.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (relInfo != null)
                    {
                        var refObjects = os.GetObjects(relInfo.TargetClrType);
                        object matched = null;
                        foreach (var refObj in refObjects)
                        {
                            var display = GetObjectDisplayText(refObj);
                            if (display.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                matched = refObj;
                                break;
                            }
                        }

                        if (matched == null)
                        {
                            var available = refObjects.Cast<object>()
                                .Take(10)
                                .Select(GetObjectDisplayText);
                            return $"{relInfo.PropertyName} '{value}' not found. Available {relInfo.TargetEntity} records: {string.Join(", ", available)}";
                        }

                        var member = typeInfo.FindMember(relInfo.PropertyName);
                        if (member != null)
                        {
                            var oldRef = member.GetValue(obj);
                            member.SetValue(obj, matched);
                            updatedProperties.Add($"{relInfo.PropertyName}: {GetObjectDisplayText(oldRef)} → {GetObjectDisplayText(matched)}");
                        }
                        continue;
                    }

                    var allProps = string.Join(", ", entityInfo.Properties.Select(p => p.Name)
                        .Concat(entityInfo.Relationships.Where(r => !r.IsCollection).Select(r => r.PropertyName)));
                    return $"Property '{key}' not found on {entityInfo.Name}. Available: {allProps}";
                }

                os.CommitChanges();
                _navigationService?.RefreshActiveView();

                var summary = string.Join(" | ", updatedProperties);
                var displayName = GetObjectDisplayText(obj);
                var result = $"{entityInfo.Name} '{displayName}' updated successfully! Changes: {summary}";
                _logger.LogInformation("[Tool:update_entity] {Result}", result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:update_entity] Error");
                return $"Error updating {entityName}: {ex.Message}";
            }
        }
    }
}
