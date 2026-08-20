using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        // -- JSON result helpers ---------------------------------------------------
        // Every tool returns one JSON object. Errors are { "error": "...", ...hints }.
        // Records carry "id" (the XAF key) so follow-up tools can address them directly.

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

        private static string Json(object payload) => JsonSerializer.Serialize(payload, JsonOpts);

        private static string Error(string message) => Json(new { error = message });

        private List<string> EntityNames() => _schemaService.Schema.Entities.Select(e => e.Name).ToList();

        private string UnknownEntity(string entityName) =>
            Json(new
            {
                error = string.IsNullOrWhiteSpace(entityName)
                    ? "Entity name is required."
                    : $"Entity '{entityName}' not found.",
                availableEntities = EntityNames(),
            });

        private static List<string> SettableNames(EntityInfo entityInfo) =>
            entityInfo.Properties.Select(p => p.Name)
                .Concat(entityInfo.Relationships.Where(r => !r.IsCollection).Select(r => r.PropertyName))
                .ToList();

        private static object KeyOf(object obj, ITypeInfo typeInfo) => typeInfo.KeyMember?.GetValue(obj);

        /// <summary>
        /// Projects an entity object to an ordered dictionary: id, scalar properties (raw CLR values),
        /// then to-one references as display text.
        /// </summary>
        private static Dictionary<string, object> ToRecord(object obj, EntityInfo entityInfo, ITypeInfo typeInfo)
        {
            var record = new Dictionary<string, object> { ["id"] = KeyOf(obj, typeInfo) };
            foreach (var prop in entityInfo.Properties)
            {
                var member = typeInfo.FindMember(prop.Name);
                if (member != null) record[prop.Name] = member.GetValue(obj);
            }
            foreach (var rel in entityInfo.Relationships.Where(r => !r.IsCollection))
            {
                var member = typeInfo.FindMember(rel.PropertyName);
                if (member == null) continue;
                var refObj = member.GetValue(obj);
                record[rel.PropertyName] = refObj == null ? null : GetObjectDisplayText(refObj);
            }
            return record;
        }

        /// <summary>
        /// Attempts to produce a human-readable label for an entity object
        /// by looking for common "name" properties.
        /// </summary>
        private static string GetObjectDisplayText(object obj)
        {
            if (obj == null) return null;
            var type = obj.GetType();
            foreach (var propName in new[] { "Name", "CompanyName", "FullName", "FirstName", "Title", "InvoiceNumber", "Description" })
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

        /// <summary>
        /// Finds a to-one reference target by matching the search term against its display text.
        /// Returns the match, or a JSON error listing available records.
        /// </summary>
        private (object Match, string Error) FindReference(IObjectSpace os, RelationshipInfo relInfo, string value)
        {
            var refObjects = os.GetObjects(relInfo.TargetClrType).Cast<object>().ToList();
            var matched = refObjects.FirstOrDefault(r =>
                GetObjectDisplayText(r)?.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
            if (matched != null) return (matched, null);
            return (null, Json(new
            {
                error = $"{relInfo.PropertyName} '{value}' not found.",
                available = refObjects.Take(10).Select(GetObjectDisplayText).ToList(),
            }));
        }

        // -- Tool implementations --------------------------------------------------

        [Description("List all available entities (tables) in the database with their properties and relationships. Returns JSON.")]
        private string ListEntities()
        {
            _logger.LogInformation("[Tool:list_entities] Called");
            try
            {
                var entities = _schemaService.Schema.Entities.Select(e => new
                {
                    name = e.Name,
                    description = string.IsNullOrEmpty(e.Description) ? null : e.Description,
                    properties = e.Properties.Select(p => p.Name).ToList(),
                    relationships = e.Relationships.Select(r => new
                    {
                        property = r.PropertyName,
                        kind = r.IsCollection ? "hasMany" : "belongsTo",
                        target = r.TargetEntity,
                    }).ToList(),
                    enums = e.Properties.Where(p => p.EnumValues.Count > 0)
                        .ToDictionary(p => p.Name, p => p.EnumValues) is { Count: > 0 } d ? d : null,
                }).ToList();

                var result = Json(new { entities });
                _logger.LogInformation("[Tool:list_entities] Returning {Count} entities", entities.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:list_entities] Error");
                return Error($"Error listing entities: {ex.Message}");
            }
        }

        [Description("Get full schema details for a single entity — properties, types, relationships, and enum values. Call this before querying or creating records of an unfamiliar entity. Returns JSON.")]
        private string DescribeEntity(
            [Description("Entity name to describe (e.g. 'Customer', 'Order'). Use list_entities to see available names.")] string entityName)
        {
            _logger.LogInformation("[Tool:describe_entity] Called with entity={Entity}", entityName);
            try
            {
                var entityInfo = _schemaService.Schema.FindEntity(entityName ?? "");
                if (entityInfo == null) return UnknownEntity(entityName);

                var result = Json(new
                {
                    name = entityInfo.Name,
                    description = string.IsNullOrEmpty(entityInfo.Description) ? null : entityInfo.Description,
                    properties = entityInfo.Properties.Select(p => new
                    {
                        name = p.Name,
                        type = p.TypeName,
                        required = p.IsRequired,
                        description = string.IsNullOrEmpty(p.Description) ? null : p.Description,
                        values = p.EnumValues.Count > 0 ? p.EnumValues : null,
                    }).ToList(),
                    relationships = entityInfo.Relationships.Select(r => new
                    {
                        property = r.PropertyName,
                        kind = r.IsCollection ? "hasMany" : "belongsTo",
                        target = r.TargetEntity,
                    }).ToList(),
                });
                _logger.LogInformation("[Tool:describe_entity] Returning {Len} chars for {Entity}", result.Length, entityName);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:describe_entity] Error");
                return Error($"Error describing {entityName}: {ex.Message}");
            }
        }

        [Description("Query records of any entity (table) in the database. Call describe_entity first if you are unsure about property names or types. Returns JSON: { entity, count, truncated?, records: [{ id, ...properties, ...references }] }.")]
        private string QueryEntity(
            [Description("Entity name to query (e.g. 'Customer', 'Order', 'Product'). Use list_entities to see available names.")] string entityName,
            [Description("Optional filter as semicolon-separated 'PropertyName=value' pairs. Example: 'Status=New;Country=USA'. Omit for no filter.")] string filter = "",
            [Description("Maximum number of records to return. Default is 25.")] int top = 25)
        {
            _logger.LogInformation("[Tool:query_entity] Called with entity={Entity}, filter={Filter}, top={Top}", entityName, filter, top);
            try
            {
                var entityInfo = _schemaService.Schema.FindEntity(entityName ?? "");
                if (entityInfo == null) return UnknownEntity(entityName);

                var entityType = entityInfo.ClrType;
                if (top <= 0) top = 25;

                using var sos = GetObjectSpace(entityType);
                var os = sos.Os;
                var typeInfo = XafTypesInfo.Instance.FindTypeInfo(entityType);

                // ponytail: load-all + in-memory filter; fine for a demo-sized DB,
                // switch to criteria-based GetObjects when row counts matter.
                IEnumerable<object> results = os.GetObjects(entityType).Cast<object>();

                foreach (var (key, value) in ParsePairs(filter))
                {
                    var propInfo = entityInfo.Properties
                        .FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (propInfo != null)
                    {
                        var member = typeInfo.FindMember(propInfo.Name);
                        if (member == null) continue;
                        if (propInfo.ClrType == typeof(string))
                        {
                            results = results.Where(o =>
                                member.GetValue(o) is string v && v.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
                        }
                        else
                        {
                            object converted;
                            try { converted = ConvertValue(value, propInfo.ClrType); }
                            catch
                            {
                                return Error($"Cannot convert filter value '{value}' to type '{propInfo.TypeName}' for property '{key}'.");
                            }
                            results = results.Where(o => Equals(member.GetValue(o), converted));
                        }
                        continue;
                    }

                    var relInfo = entityInfo.Relationships
                        .FirstOrDefault(r => !r.IsCollection && r.PropertyName.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (relInfo != null)
                    {
                        var member = typeInfo.FindMember(relInfo.PropertyName);
                        if (member != null)
                        {
                            results = results.Where(o =>
                                GetObjectDisplayText(member.GetValue(o))?.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
                        }
                        continue;
                    }

                    return Json(new
                    {
                        error = $"Property '{key}' not found on {entityInfo.Name}.",
                        availableProperties = SettableNames(entityInfo),
                    });
                }

                var list = results.Take(top + 1).ToList();
                var truncated = list.Count > top;
                if (truncated) list.RemoveAt(top);

                var result = Json(new
                {
                    entity = entityInfo.Name,
                    count = list.Count,
                    truncated = truncated ? true : (bool?)null,
                    records = list.Select(o => ToRecord(o, entityInfo, typeInfo)).ToList(),
                });
                _logger.LogInformation("[Tool:query_entity] Returning {Len} chars, {Count} records", result.Length, list.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:query_entity] Error");
                return Error($"Error querying {entityName}: {ex.Message}");
            }
        }

        [Description("Create a new record of any entity in the database. Call describe_entity first to see required fields, property types, and relationships. Returns JSON: { entity, id, created, values }.")]
        private string CreateEntity(
            [Description("Entity name to create (e.g. 'Customer', 'Order', 'Product'). Use list_entities to see available names.")] string entityName,
            [Description("Semicolon-separated 'PropertyName=value' pairs. For reference properties (relationships), provide a search term to match by name. Example: 'CompanyName=Acme Corp;Country=USA' or 'Customer=Acme;Status=New'.")] string properties)
        {
            _logger.LogInformation("[Tool:create_entity] Called with entity={Entity}, properties={Props}", entityName, properties);
            try
            {
                var entityInfo = _schemaService.Schema.FindEntity(entityName ?? "");
                if (entityInfo == null) return UnknownEntity(entityName);

                if (string.IsNullOrWhiteSpace(properties))
                    return Json(new { error = "Properties are required.", availableProperties = SettableNames(entityInfo) });

                var entityType = entityInfo.ClrType;
                using var sos = GetObjectSpace(entityType);
                var os = sos.Os;
                var typeInfo = XafTypesInfo.Instance.FindTypeInfo(entityType);

                var obj = os.CreateObject(entityType);
                var values = new Dictionary<string, object>();

                foreach (var (key, value) in ParsePairs(properties))
                {
                    var propInfo = entityInfo.Properties
                        .FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (propInfo != null)
                    {
                        var member = typeInfo.FindMember(propInfo.Name);
                        if (member == null) continue;
                        try
                        {
                            var converted = ConvertValue(value, propInfo.ClrType);
                            member.SetValue(obj, converted);
                            values[propInfo.Name] = converted;
                        }
                        catch (Exception ex)
                        {
                            return Error($"Error setting {propInfo.Name}: cannot convert '{value}' to {propInfo.TypeName}. {ex.Message}");
                        }
                        continue;
                    }

                    var relInfo = entityInfo.Relationships
                        .FirstOrDefault(r => !r.IsCollection && r.PropertyName.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (relInfo != null)
                    {
                        var (matched, error) = FindReference(os, relInfo, value);
                        if (error != null) return error;
                        var member = typeInfo.FindMember(relInfo.PropertyName);
                        if (member != null)
                        {
                            member.SetValue(obj, matched);
                            values[relInfo.PropertyName] = GetObjectDisplayText(matched);
                        }
                        continue;
                    }

                    return Json(new
                    {
                        error = $"Property '{key}' not found on {entityInfo.Name}.",
                        availableProperties = SettableNames(entityInfo),
                    });
                }

                os.CommitChanges();
                _navigationService?.RefreshActiveView();

                var result = Json(new { entity = entityInfo.Name, id = KeyOf(obj, typeInfo), created = true, values });
                _logger.LogInformation("[Tool:create_entity] {Result}", result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:create_entity] Error");
                return Error($"Error creating {entityName}: {ex.Message}");
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
                var entityInfo = _schemaService.Schema.FindEntity(entityName ?? "");
                if (entityInfo == null) return UnknownEntity(entityName);

                _navigationService.NavigateToListView(entityName);
                return Json(new { action = "navigate_to_list", ok = true, entity = entityInfo.Name });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:navigate_to_list] Error");
                return Error($"Error navigating to {entityName}: {ex.Message}");
            }
        }

        [Description("Navigate the user's application to a specific record's detail view. Use this when the user wants to open or view a particular record.")]
        private string NavigateToDetail(
            [Description("Entity name (e.g. 'Customer', 'Order'). Use list_entities to see available names.")] string entityName,
            [Description("The record identifier — the 'id' from a query_entity record (preferred), or a search term to match by name.")] string identifier)
        {
            _logger.LogInformation("[Tool:navigate_to_detail] Called with entity={Entity}, id={Id}", entityName, identifier);
            try
            {
                var entityInfo = _schemaService.Schema.FindEntity(entityName ?? "");
                if (entityInfo == null) return UnknownEntity(entityName);

                if (string.IsNullOrWhiteSpace(identifier))
                    return Error("An identifier (id or search term) is required to find the record.");

                _navigationService.NavigateToDetailView(entityName, identifier);
                return Json(new { action = "navigate_to_detail", ok = true, entity = entityInfo.Name, identifier });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:navigate_to_detail] Error");
                return Error($"Error navigating to {entityName} detail: {ex.Message}");
            }
        }

        // -- Active view tools ---------------------------------------------------------

        [Description("Get information about what the user is currently viewing in the application. Returns JSON with the entity name, view type (list or detail), view ID, and for detail views the specific record being viewed. Always call this first when the user refers to 'this record', 'the current view', 'this list', etc.")]
        private string GetActiveView()
        {
            _logger.LogInformation("[Tool:get_active_view] Called");
            try
            {
                if (_activeViewContext == null || _activeViewContext.EntityName == null)
                    return Error("No active view context available.");

                var entityInfo = _schemaService.Schema.FindEntity(_activeViewContext.EntityName);
                var isList = _activeViewContext.IsListView;

                object record = null;
                if (!isList && _activeViewContext.CurrentObjectDisplay != null)
                {
                    Dictionary<string, object> fields = null;
                    if (entityInfo != null && _activeViewContext.CurrentObjectKey != null)
                    {
                        try
                        {
                            using var sos = GetObjectSpace(entityInfo.ClrType);
                            var typeInfo = XafTypesInfo.Instance.FindTypeInfo(entityInfo.ClrType);
                            var key = ConvertValue(_activeViewContext.CurrentObjectKey, typeInfo.KeyMember.MemberType);
                            var obj = sos.Os.GetObjectByKey(entityInfo.ClrType, key);
                            if (obj != null) fields = ToRecord(obj, entityInfo, typeInfo);
                        }
                        catch
                        {
                            // Best effort — don't fail the tool if we can't load the record
                        }
                    }
                    record = new
                    {
                        id = _activeViewContext.CurrentObjectKey,
                        display = _activeViewContext.CurrentObjectDisplay,
                        fields,
                    };
                }

                return Json(new
                {
                    entity = _activeViewContext.EntityName,
                    viewType = isList ? "list" : "detail",
                    viewId = _activeViewContext.ViewId,
                    record,
                    filterableProperties = isList && entityInfo != null ? entityInfo.Properties.Select(p => p.Name).ToList() : null,
                    filterableRelationships = isList && entityInfo != null
                        ? entityInfo.Relationships.Where(r => !r.IsCollection).Select(r => r.PropertyName).ToList() is { Count: > 0 } rels ? rels : null
                        : null,
                    editableProperties = !isList && entityInfo != null ? SettableNames(entityInfo) : null,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:get_active_view] Error");
                return Error($"Error getting active view: {ex.Message}");
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
                    return Error("No active list view to filter. Use navigate_to_list first to open a list view.");

                if (string.IsNullOrWhiteSpace(criteria))
                    return Error("A criteria expression is required. Example: [Category.Name] = 'Grains'");

                _navigationService.FilterActiveList(criteria);
                return Json(new { action = "filter_active_list", ok = true, entity = _activeViewContext.EntityName, criteria });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:filter_active_list] Error");
                return Error($"Error filtering list: {ex.Message}");
            }
        }

        [Description("Remove the AI-applied filter from the currently active list view, showing all records again.")]
        private string ClearActiveListFilter()
        {
            _logger.LogInformation("[Tool:clear_active_list_filter] Called");
            try
            {
                if (_activeViewContext == null || !_activeViewContext.IsListView)
                    return Error("No active list view to clear filter from.");

                _navigationService.ClearActiveListFilter();
                return Json(new { action = "clear_active_list_filter", ok = true, entity = _activeViewContext.EntityName });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:clear_active_list_filter] Error");
                return Error($"Error clearing filter: {ex.Message}");
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
                return Json(new { action = "save_active_view", ok = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:save_active_view] Error");
                return Error($"Error saving: {ex.Message}");
            }
        }

        [Description("Close the currently active view and return to the previous view. Use this when the user says 'close', 'go back', 'close this view', etc.")]
        private string CloseActiveView()
        {
            _logger.LogInformation("[Tool:close_active_view] Called");
            try
            {
                _navigationService.CloseActiveView();
                return Json(new { action = "close_active_view", ok = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:close_active_view] Error");
                return Error($"Error closing view: {ex.Message}");
            }
        }

        // -- Update tool ---------------------------------------------------------------

        [Description("Update (modify) an existing record in the database. Use get_active_view first to find the current record's id when the user says 'this record' or 'change this'. You can also update any record by providing its entity name and identifier. Returns JSON: { entity, id, display, updated, changes: { Property: { from, to } } }.")]
        private string UpdateEntity(
            [Description("Entity name (e.g. 'Customer', 'Supplier', 'Product'). Use list_entities to see available names.")] string entityName,
            [Description("The record identifier — the 'id' from a query_entity record or get_active_view (preferred), or a search term to match by name.")] string identifier,
            [Description("Semicolon-separated 'PropertyName=value' pairs for fields to update. Example: 'ContactName=Just Testing;Country=Netherlands'. For reference properties, provide a search term to match by name.")] string properties)
        {
            _logger.LogInformation("[Tool:update_entity] Called with entity={Entity}, id={Id}, properties={Props}", entityName, identifier, properties);
            try
            {
                var entityInfo = _schemaService.Schema.FindEntity(entityName ?? "");
                if (entityInfo == null) return UnknownEntity(entityName);

                if (string.IsNullOrWhiteSpace(identifier))
                    return Error("An identifier (id or search term) is required. Use get_active_view to get the id of the current record.");

                if (string.IsNullOrWhiteSpace(properties))
                    return Json(new { error = "Properties to update are required.", availableProperties = SettableNames(entityInfo) });

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

                obj ??= os.GetObjects(entityType).Cast<object>().FirstOrDefault(c =>
                    GetObjectDisplayText(c)?.IndexOf(identifier, StringComparison.OrdinalIgnoreCase) >= 0);

                if (obj == null)
                    return Error($"No {entityInfo.Name} record found matching '{identifier}'.");

                var changes = new Dictionary<string, object>();

                foreach (var (key, value) in ParsePairs(properties))
                {
                    var propInfo = entityInfo.Properties
                        .FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (propInfo != null)
                    {
                        var member = typeInfo.FindMember(propInfo.Name);
                        if (member == null) continue;
                        try
                        {
                            var oldVal = member.GetValue(obj);
                            var converted = ConvertValue(value, propInfo.ClrType);
                            member.SetValue(obj, converted);
                            changes[propInfo.Name] = new { from = oldVal, to = converted };
                        }
                        catch (Exception ex)
                        {
                            return Error($"Error setting {propInfo.Name}: cannot convert '{value}' to {propInfo.TypeName}. {ex.Message}");
                        }
                        continue;
                    }

                    var relInfo = entityInfo.Relationships
                        .FirstOrDefault(r => !r.IsCollection && r.PropertyName.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (relInfo != null)
                    {
                        var (matched, error) = FindReference(os, relInfo, value);
                        if (error != null) return error;
                        var member = typeInfo.FindMember(relInfo.PropertyName);
                        if (member != null)
                        {
                            var oldRef = member.GetValue(obj);
                            member.SetValue(obj, matched);
                            changes[relInfo.PropertyName] = new { from = GetObjectDisplayText(oldRef), to = GetObjectDisplayText(matched) };
                        }
                        continue;
                    }

                    return Json(new
                    {
                        error = $"Property '{key}' not found on {entityInfo.Name}.",
                        availableProperties = SettableNames(entityInfo),
                    });
                }

                os.CommitChanges();
                _navigationService?.RefreshActiveView();

                var result = Json(new
                {
                    entity = entityInfo.Name,
                    id = KeyOf(obj, typeInfo),
                    display = GetObjectDisplayText(obj),
                    updated = true,
                    changes,
                });
                _logger.LogInformation("[Tool:update_entity] {Result}", result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:update_entity] Error");
                return Error($"Error updating {entityName}: {ex.Message}");
            }
        }
    }
}
