using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Security;
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
        private readonly AILogScope _log;
        private List<AIFunction> _tools;

        /// <summary>
        /// When set (WinForms), ObjectSpaces come from <c>Application.CreateObjectSpace</c> (secured,
        /// the logged-on user's). Requires <see cref="Dispatch"/> so the call lands on the UI thread.
        /// Blazor uses the scope's <see cref="IObjectSpaceFactory"/> instead.
        /// </summary>
        public XafApplication Application { get; set; }

        /// <summary>
        /// Runs a whole tool body (ObjectSpace, query or mutation, projection, dispose) where the
        /// platform wants XAF work: the circuit's synchronization context in Blazor
        /// (<c>BlazorApplication.InvokeAsync</c>), the UI thread in WinForms. Set by the platform's
        /// executor on activation; null runs the body inline (tests, the Debug test API).
        /// Tool calls arrive on the LLM client's continuation, so without this they would run on
        /// a thread-pool thread against the circuit's scoped services.
        /// </summary>
        public Func<Func<Task<object>>, Task<object>> Dispatch { get; set; }

        public AIToolsProvider(IServiceProvider serviceProvider, SchemaDiscoveryService schemaService,
            INavigationService navigationService = null, ActiveViewContext activeViewContext = null,
            AILogScope log = null)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _schemaService = schemaService ?? throw new ArgumentNullException(nameof(schemaService));
            _logger = serviceProvider.GetRequiredService<ILogger<AIToolsProvider>>();
            _navigationService = navigationService;
            _activeViewContext = activeViewContext;
            _log = log;
        }

        public IReadOnlyList<AIFunction> Tools => _tools ??= CreateTools();

        private List<AIFunction> CreateTools()
        {
            var tools = new List<AIFunction>
            {
                Tool(ListEntities, "list_entities"),
                Tool(DescribeEntity, "describe_entity"),
                Tool(QueryEntity, "query_entity"),
                Tool(CreateEntity, "create_entity"),
            };

            if (_navigationService != null)
            {
                tools.Add(Tool(NavigateToList, "navigate_to_list"));
                tools.Add(Tool(NavigateToDetail, "navigate_to_detail"));
                tools.Add(Tool(FilterActiveList, "filter_active_list"));
                tools.Add(Tool(ClearActiveListFilter, "clear_active_list_filter"));
                tools.Add(Tool(SaveActiveView, "save_active_view"));
                tools.Add(Tool(CloseActiveView, "close_active_view"));
            }

            if (_activeViewContext != null)
            {
                tools.Add(Tool(GetActiveView, "get_active_view"));
                tools.Add(Tool(UpdateEntity, "update_entity"));
            }

            return tools;
        }

        private AIFunction Tool(Delegate method, string name) =>
            new DispatchedFunction(AIFunctionFactory.Create(method, name), this);

        /// <summary>
        /// Routes every invocation through <see cref="Dispatch"/> when one is set and records the
        /// call in this scope's <see cref="AILogScope"/>.
        /// </summary>
        private sealed class DispatchedFunction(AIFunction inner, AIToolsProvider owner) : DelegatingAIFunction(inner)
        {
            protected override async ValueTask<object> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            {
                var dispatch = owner.Dispatch;
                var log = owner._log;
                // The scope may be cleared while this call runs (turn reset, WinForms logoff): an
                // entry written for the discarded conversation must not land in the next one.
                var generation = log?.Generation ?? 0;
                var outcome = dispatch == null
                    ? await GuardedAsync(arguments, cancellationToken)
                    : await dispatch(() => GuardedAsync(arguments, cancellationToken));

                if (outcome is ExceptionDispatchInfo failure)
                {
                    if (failure.SourceException is not OperationCanceledException)
                        log?.Add(generation, LogLevel.Error, "Tools", $"{Name}({Args(arguments)}) failed: {failure.SourceException.Message}");
                    failure.Throw();
                }

                // Tool bodies catch their own exceptions and answer { "error": ... }, and UI tools answer
                // { ok: false, error } when the window refused (AI-007): both are warnings, not results.
                var text = outcome?.ToString();
                var failed = text != null && (text.StartsWith("{\"error\"", StringComparison.Ordinal) || text.Contains("\"ok\":false", StringComparison.Ordinal));
                var level = failed ? LogLevel.Warning : LogLevel.Information;
                log?.Add(generation, level, "Tools", $"{Name}({Args(arguments)}) -> {Trim(text)}");
                return outcome;
            }

            /// <summary>
            /// Nothing may throw across the platform dispatcher: an exception escaping
            /// BlazorApplication.InvokeAsync takes the circuit down (argument binding, e.g.
            /// top="abc", throws before the tool body's own catch). Capture, rethrow on the caller's side.
            /// </summary>
            private async Task<object> GuardedAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            {
                try
                {
                    // Recheck: the turn may have been cancelled or reset (WinForms logoff) while
                    // this call waited for the UI thread; the body must not run for the next user.
                    cancellationToken.ThrowIfCancellationRequested();
                    return await base.InvokeCoreAsync(arguments, cancellationToken);
                }
                catch (Exception ex)
                {
                    return ExceptionDispatchInfo.Capture(ex);
                }
            }

            private static string Args(AIFunctionArguments arguments) =>
                JsonSerializer.Serialize(arguments.ToDictionary(kv => kv.Key, kv => kv.Value), JsonOpts);

            // ponytail: the panel shows one line per call; a 25-record query result is enough at 4 KB.
            private static string Trim(string s) => s == null ? "null" : s.Length <= 4000 ? s : s[..4000] + "…";
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
        /// The logged-on user's secured object space (SEC-001): the application's in WinForms, the
        /// scope's <see cref="IObjectSpaceFactory"/> in Blazor (this provider is scoped per circuit,
        /// so the factory carries that circuit's user). Callers dispose the returned wrapper.
        /// </summary>
        private ScopedObjectSpace GetObjectSpace(Type entityType)
        {
            if (Application != null)
                return new ScopedObjectSpace(Application.CreateObjectSpace(entityType));   // Dispatch put us on the UI thread

            return new ScopedObjectSpace(_serviceProvider.GetRequiredService<IObjectSpaceFactory>().CreateObjectSpace(entityType));
        }

        /// <summary>Wraps an IObjectSpace for disposal at the end of a tool body.</summary>
        private sealed class ScopedObjectSpace(IObjectSpace os) : IDisposable
        {
            public IObjectSpace Os { get; } = os;
            public void Dispose() => Os.Dispose();
        }

        // -- Permissions ------------------------------------------------------------
        // A secured object space filters reads silently (a denied row is absent, a denied member
        // reads as its default value) and rejects unauthorised writes on its own terms, so a tool
        // asks first and answers "permission denied" in one shape the model can act on.

        private IRequestSecurityStrategy Security =>
            (Application?.Security ?? _serviceProvider.GetService<ISecurityStrategyBase>()) as IRequestSecurityStrategy;

        private bool CanRead(Type type, IObjectSpace os) => Security?.CanRead(type, os) ?? true;
        private bool CanReadMember(IObjectSpace os, object obj, string member) => Security?.CanRead(os, obj, member) ?? true;
        private bool CanCreate(Type type, IObjectSpace os) => Security?.CanCreate(type, os) ?? true;
        private bool CanWrite(IObjectSpace os, object obj, string member = null) => Security?.CanWrite(os, obj, member) ?? true;

        private static string PermissionDenied(string entity, string operation, string member = null) =>
            Json(new { error = "permission denied", entity, operation, member });

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
        /// then to-one references as display text. A member the user may not read is left out:
        /// the secured space would hand back the type's default value as if it were real.
        /// </summary>
        private Dictionary<string, object> ToRecord(object obj, EntityInfo entityInfo, ITypeInfo typeInfo, IObjectSpace os)
        {
            var record = new Dictionary<string, object> { ["id"] = KeyOf(obj, typeInfo) };
            foreach (var prop in entityInfo.Properties)
            {
                var member = typeInfo.FindMember(prop.Name);
                if (member != null && CanReadMember(os, obj, prop.Name)) record[prop.Name] = member.GetValue(obj);
            }
            foreach (var rel in entityInfo.Relationships.Where(r => !r.IsCollection))
            {
                var member = typeInfo.FindMember(rel.PropertyName);
                if (member == null || !CanReadMember(os, obj, rel.PropertyName)) continue;
                var refObj = member.GetValue(obj);
                record[rel.PropertyName] = refObj == null ? null : GetObjectDisplayText(refObj);
            }
            return record;
        }

        /// <summary>
        /// Attempts to produce a human-readable label for an entity object
        /// by looking for common "name" properties.
        /// </summary>
        private static string GetObjectDisplayText(object obj) => DisplayText.Of(obj);

        /// <summary>
        /// JSON error for a name that matches several records: the model gets ids to disambiguate with.
        /// </summary>
        private static string AmbiguousError(string what, string term, List<object> candidates, Type type)
        {
            var typeInfo = XafTypesInfo.Instance.FindTypeInfo(type);
            return Json(new
            {
                error = $"{what} '{term}' is ambiguous: {candidates.Count} records match. Use the exact name or the id.",
                candidates = candidates.Take(10).Select(c => new { id = KeyOf(c, typeInfo), display = GetObjectDisplayText(c) }).ToList(),
            });
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
            if (!CanRead(relInfo.TargetClrType, os))
                return (null, PermissionDenied(relInfo.TargetEntity, "read"));   // not "not found": the user cannot see the targets
            var (matched, candidates) = FindRecord(os, relInfo.TargetClrType, value);
            if (matched != null) return (matched, null);
            if (candidates.Count > 1)
                return (null, AmbiguousError(relInfo.PropertyName, value, candidates, relInfo.TargetClrType));
            return (null, Json(new
            {
                error = $"{relInfo.PropertyName} '{value}' not found.",
                available = os.GetObjects(relInfo.TargetClrType).Cast<object>().Take(10).Select(GetObjectDisplayText).ToList(),
            }));
        }

        /// <summary>
        /// Resolves a record by its key first (the "id" every tool result carries), then by display
        /// text via <see cref="DisplayText.Resolve"/>. Same contract as that method: a null
        /// <c>Match</c> with several <c>Candidates</c> means ambiguous, with none means not found.
        /// </summary>
        private static (object Match, List<object> Candidates) FindRecord(IObjectSpace os, Type type, string identifier)
        {
            var typeInfo = XafTypesInfo.Instance.FindTypeInfo(type);
            try
            {
                var key = ConvertValue(identifier, typeInfo.KeyMember.MemberType);
                var byKey = os.GetObjectByKey(type, key);
                if (byKey != null) return (byKey, [byKey]);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
            {
                // Not a key at all: fall through to the display-text search.
            }

            return DisplayText.Resolve(os.GetObjects(type).Cast<object>(), identifier);
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
                if (!CanRead(entityType, os)) return PermissionDenied(entityInfo.Name, "read");
                var typeInfo = XafTypesInfo.Instance.FindTypeInfo(entityType);

                // ponytail: load-all + in-memory filter; fine for a demo-sized DB,
                // switch to criteria-based GetObjects when row counts matter. Row-level permissions
                // apply here: the secured space returns only the rows this user may read.
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
                    records = list.Select(o => ToRecord(o, entityInfo, typeInfo, os)).ToList(),
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
            [Description("Semicolon-separated 'PropertyName=value' pairs. For reference properties (relationships), provide the record's id or a search term to match by name. Example: 'CompanyName=Acme Corp;Country=USA' or 'Customer=Acme;Status=New'.")] string properties,
            CancellationToken cancellationToken = default)
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
                if (!CanCreate(entityType, os)) return PermissionDenied(entityInfo.Name, "create");
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
                        if (!CanWrite(os, obj, propInfo.Name)) return PermissionDenied(entityInfo.Name, "write", propInfo.Name);
                        try
                        {
                            var converted = ConvertValue(value, propInfo.ClrType);
                            member.SetValue(obj, converted);
                            values[propInfo.Name] = converted;
                        }
                        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException or ArgumentException)
                        {
                            return Error($"Error setting {propInfo.Name}: cannot convert '{value}' to {propInfo.TypeName}. {ex.Message}");
                        }
                        continue;
                    }

                    var relInfo = entityInfo.Relationships
                        .FirstOrDefault(r => !r.IsCollection && r.PropertyName.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (relInfo != null)
                    {
                        if (!CanWrite(os, obj, relInfo.PropertyName)) return PermissionDenied(entityInfo.Name, "write", relInfo.PropertyName);
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

                cancellationToken.ThrowIfCancellationRequested(); // AI-008: a stopped turn must not commit
                os.CommitChanges();
                _navigationService?.RefreshActiveView();

                var result = Json(new { entity = entityInfo.Name, id = KeyOf(obj, typeInfo), created = true, values });
                _logger.LogInformation("[Tool:create_entity] {Result}", result);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
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

                var ui = _navigationService.NavigateToListView(entityName);
                return Json(new { action = "navigate_to_list", ok = ui.Ok, error = ui.Error, entity = entityInfo.Name });
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

                // Resolve here, not in the executor: the executor runs later on the UI thread and can
                // only log. The model needs "ambiguous" / "not found" as the tool result (AI-006).
                object key;
                string display;
                using (var sos = GetObjectSpace(entityInfo.ClrType))
                {
                    if (!CanRead(entityInfo.ClrType, sos.Os)) return PermissionDenied(entityInfo.Name, "read");
                    var (match, candidates) = FindRecord(sos.Os, entityInfo.ClrType, identifier);
                    if (match == null && candidates.Count > 1)
                        return AmbiguousError(entityInfo.Name, identifier, candidates, entityInfo.ClrType);
                    if (match == null)
                        return Error($"No {entityInfo.Name} record found matching '{identifier}'.");
                    key = KeyOf(match, XafTypesInfo.Instance.FindTypeInfo(entityInfo.ClrType));
                    display = GetObjectDisplayText(match);
                }

                var ui = _navigationService.NavigateToDetailView(entityName, key?.ToString() ?? identifier);
                return Json(new { action = "navigate_to_detail", ok = ui.Ok, error = ui.Error, entity = entityInfo.Name, identifier, id = key, display });
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
                    // The view context caches the record's id and display text from when the view
                    // opened; re-read through the secured space so a revoked permission or a row
                    // the user may no longer see is not echoed back from the cache.
                    Dictionary<string, object> fields = null;
                    var visible = false;
                    if (entityInfo != null && _activeViewContext.CurrentObjectKey != null)
                    {
                        try
                        {
                            using var sos = GetObjectSpace(entityInfo.ClrType);
                            if (!CanRead(entityInfo.ClrType, sos.Os)) return PermissionDenied(entityInfo.Name, "read");
                            var typeInfo = XafTypesInfo.Instance.FindTypeInfo(entityInfo.ClrType);
                            var key = ConvertValue(_activeViewContext.CurrentObjectKey, typeInfo.KeyMember.MemberType);
                            var obj = sos.Os.GetObjectByKey(entityInfo.ClrType, key);
                            if (obj != null)
                            {
                                visible = true;
                                fields = ToRecord(obj, entityInfo, typeInfo, sos.Os);
                            }
                        }
                        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
                        {
                            // The key in the view context is not this type's key: fields stay null.
                        }
                    }
                    record = visible
                        ? new { id = _activeViewContext.CurrentObjectKey, display = _activeViewContext.CurrentObjectDisplay, fields }
                        : new { id = (string)null, display = (string)null, fields, note = "The current record is not readable for this user." };
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

                var ui = _navigationService.FilterActiveList(criteria);
                return Json(new { action = "filter_active_list", ok = ui.Ok, error = ui.Error, entity = _activeViewContext.EntityName, criteria });
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

                var ui = _navigationService.ClearActiveListFilter();
                return Json(new { action = "clear_active_list_filter", ok = ui.Ok, error = ui.Error, entity = _activeViewContext.EntityName });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:clear_active_list_filter] Error");
                return Error($"Error clearing filter: {ex.Message}");
            }
        }

        // -- Save / Close tools --------------------------------------------------------

        [Description("Save (commit) changes in the currently active detail view, after validation. Use this when the user says 'save', 'save this', 'save changes', etc. Returns JSON { ok, error? }: when ok is false, error says why; a validation or database rejection means nothing was saved, while 'has not finished' means the outcome is unknown and the data must be checked before repeating.")]
        private string SaveActiveView()
        {
            _logger.LogInformation("[Tool:save_active_view] Called");
            try
            {
                var ui = _navigationService.SaveActiveView();
                return Json(new { action = "save_active_view", ok = ui.Ok, error = ui.Error });
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
                var ui = _navigationService.CloseActiveView();
                return Json(new { action = "close_active_view", ok = ui.Ok, error = ui.Error });
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
            [Description("Semicolon-separated 'PropertyName=value' pairs for fields to update. Example: 'ContactName=Just Testing;Country=Netherlands'. For reference properties, provide the record's id or a search term to match by name.")] string properties,
            CancellationToken cancellationToken = default)
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
                if (!CanRead(entityType, os)) return PermissionDenied(entityInfo.Name, "read");
                var typeInfo = XafTypesInfo.Instance.FindTypeInfo(entityType);

                // Key first (the id from query_entity / get_active_view), then display text. A row
                // this user may not read is not in the secured space at all: "not found" is the truth.
                var (obj, candidates) = FindRecord(os, entityType, identifier);
                if (obj == null && candidates.Count > 1)
                    return AmbiguousError(entityInfo.Name, identifier, candidates, entityType);
                if (obj == null)
                    return Error($"No {entityInfo.Name} record found matching '{identifier}'.");
                if (!CanWrite(os, obj)) return PermissionDenied(entityInfo.Name, "write");

                var changes = new Dictionary<string, object>();

                foreach (var (key, value) in ParsePairs(properties))
                {
                    var propInfo = entityInfo.Properties
                        .FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (propInfo != null)
                    {
                        var member = typeInfo.FindMember(propInfo.Name);
                        if (member == null) continue;
                        if (!CanWrite(os, obj, propInfo.Name)) return PermissionDenied(entityInfo.Name, "write", propInfo.Name);
                        try
                        {
                            var oldVal = member.GetValue(obj);
                            var converted = ConvertValue(value, propInfo.ClrType);
                            member.SetValue(obj, converted);
                            changes[propInfo.Name] = new { from = oldVal, to = converted };
                        }
                        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException or ArgumentException)
                        {
                            return Error($"Error setting {propInfo.Name}: cannot convert '{value}' to {propInfo.TypeName}. {ex.Message}");
                        }
                        continue;
                    }

                    var relInfo = entityInfo.Relationships
                        .FirstOrDefault(r => !r.IsCollection && r.PropertyName.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (relInfo != null)
                    {
                        if (!CanWrite(os, obj, relInfo.PropertyName)) return PermissionDenied(entityInfo.Name, "write", relInfo.PropertyName);
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

                cancellationToken.ThrowIfCancellationRequested(); // AI-008: a stopped turn must not commit
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tool:update_entity] Error");
                return Error($"Error updating {entityName}: {ex.Message}");
            }
        }
    }
}
