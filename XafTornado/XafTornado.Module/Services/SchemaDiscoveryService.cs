using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Reflection;
using System.Text;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using XafTornado.Module.Attributes;

namespace XafTornado.Module.Services
{
    /// <summary>
    /// Discovers entity metadata at runtime via XAF <see cref="ITypesInfo"/>
    /// and generates a system prompt describing the full data model.
    /// </summary>
    public sealed class SchemaDiscoveryService
    {
        private readonly ITypesInfo _typesInfo;
        private readonly object _lock = new();
        private SchemaInfo _cached;

        /// <summary>
        /// Whether the model uses opt-in mode (at least one entity has <see cref="AIVisibleAttribute"/>).
        /// When true, only entities decorated with <c>[AIVisible]</c> are discovered.
        /// When false, all persistent business-object entities are discovered (backward compatibility).
        /// </summary>
        private bool? _useOptInMode;

        public SchemaDiscoveryService(ITypesInfo typesInfo)
        {
            _typesInfo = typesInfo ?? throw new ArgumentNullException(nameof(typesInfo));
        }

        /// <summary>
        /// Returns cached entity metadata, discovering it on first call.
        /// </summary>
        public SchemaInfo Schema
        {
            get
            {
                if (_cached != null) return _cached;
                lock (_lock)
                {
                    return _cached ??= Discover();
                }
            }
        }

        /// <summary>
        /// Clears the cached schema so the next access re-discovers entities.
        /// Call this after XAF types are fully registered (e.g. after <c>Application.Setup()</c>).
        /// </summary>
        public void InvalidateCache()
        {
            lock (_lock) { _cached = null; }
        }

        /// <summary>
        /// Generates a Markdown system prompt describing all discovered entities,
        /// their properties, relationships, and enum values.
        /// </summary>
        /// <summary>
        /// Generates a lightweight system prompt listing only entity names and descriptions.
        /// Full property details are available on demand via the <c>describe_entity</c> tool.
        /// </summary>
        public string GenerateSystemPrompt()
        {
            var schema = Schema;
            var sb = new StringBuilder();

            sb.AppendLine("You are a helpful, action-oriented business assistant for an order management application.");
            sb.AppendLine($"Current date and time: {DateTime.Now:yyyy-MM-dd HH:mm} (use this for any date-related fields).");
            sb.AppendLine("IMPORTANT: Always take action by calling tools immediately. Never ask the user for confirmation before calling a tool — just do it. If the user asks you to filter, navigate, or query something, execute it right away.");
            sb.AppendLine();
            sb.AppendLine("Available entities:");

            foreach (var entity in schema.Entities)
            {
                if (!string.IsNullOrEmpty(entity.Description))
                    sb.AppendLine($"- **{entity.Name}** — {entity.Description}");
                else
                    sb.AppendLine($"- **{entity.Name}**");
            }

            sb.AppendLine();
            sb.AppendLine("Tool usage rules (follow these strictly):");
            sb.AppendLine("1. ALWAYS call `get_active_view` first when the user mentions 'this list', 'the current view', 'filter this', 'show me', or anything about what they're currently looking at. Do NOT ask what they're viewing — call the tool to find out.");
            sb.AppendLine("2. Call `describe_entity` to learn an entity's properties before querying or filtering. Do NOT guess property names.");
            sb.AppendLine("3. Call `query_entity` to fetch data. Call `create_entity` to create records.");
            sb.AppendLine("4. Call `navigate_to_list` to open an entity's list view for the user.");
            sb.AppendLine("5. Call `navigate_to_detail` to open a specific record's detail view.");
            sb.AppendLine("6. Call `filter_active_list` to filter the currently displayed list view. Use DevExpress criteria syntax, e.g. `[Category.Name] = 'Grains'` or `[Country] = 'USA'`.");
            sb.AppendLine("7. Call `clear_active_list_filter` to remove the filter and show all records again.");
            sb.AppendLine("8. Call `save_active_view` to save changes in the current detail view.");
            sb.AppendLine("9. Call `close_active_view` to close the current view and return to the previous one.");
            sb.AppendLine();
            sb.AppendLine("Behavior guidelines:");
            sb.AppendLine("- Be proactive: if the user says 'filter by USA', call `get_active_view` then `filter_active_list` immediately. Do NOT ask 'which field?' or 'are you sure?'.");
            sb.AppendLine("- Use Markdown formatting for readability (tables, bold, lists).");
            sb.AppendLine("- Be concise. Confirm what you did after doing it, not before.");

            return sb.ToString();
        }

        private SchemaInfo Discover()
        {
            var entities = new List<EntityInfo>();
            var businessObjectNamespace = "XafTornado.Module.BusinessObjects";

            // Determine if any entity in the model uses [AIVisible] (opt-in mode).
            if (_useOptInMode == null)
            {
                _useOptInMode = _typesInfo.PersistentTypes
                    .Any(t => t.Type?.GetCustomAttribute<AIVisibleAttribute>() != null);
            }

            foreach (var typeInfo in _typesInfo.PersistentTypes)
            {
                if (typeInfo.Type == null) continue;
                if (typeInfo.IsAbstract) continue;

                var aiVisible = typeInfo.Type.GetCustomAttribute<AIVisibleAttribute>();

                if (_useOptInMode.Value)
                {
                    // Opt-in mode: only include entities with [AIVisible] (and not explicitly false)
                    if (aiVisible == null || !aiVisible.IsVisible) continue;
                }
                else
                {
                    // Backward-compatible mode: include all entities from the business objects namespace
                    if (!typeInfo.Type.Namespace?.StartsWith(businessObjectNamespace, StringComparison.Ordinal) == true)
                        continue;
                }

                var aiDescription = typeInfo.Type.GetCustomAttribute<AIDescriptionAttribute>();
                var tableAttr = typeInfo.Type.GetCustomAttribute<TableAttribute>();

                var entityInfo = new EntityInfo
                {
                    Name = typeInfo.Name,
                    Description = aiDescription?.Description,
                    TableName = tableAttr?.Name ?? typeInfo.Name,
                    ClrType = typeInfo.Type,
                };

                foreach (var member in typeInfo.Members)
                {
                    if (member.Name == "ID" || member.Name == "GCRecord" || member.Name == "OptimisticLockField")
                        continue;

                    // Skip foreign key ID properties (e.g., CustomerId, EmployeeId)
                    if (member.Name.EndsWith("Id", StringComparison.Ordinal) && member.MemberType == typeof(Guid?))
                        continue;

                    // Check [AIVisible(false)] on the property to exclude it
                    var memberClrProp = typeInfo.Type.GetProperty(member.Name);
                    var memberAiVisible = memberClrProp?.GetCustomAttribute<AIVisibleAttribute>();
                    if (memberAiVisible is { IsVisible: false })
                        continue;

                    // Read [AIDescription] and [Column] on the property
                    var memberAiDescription = memberClrProp?.GetCustomAttribute<AIDescriptionAttribute>();
                    var columnAttr = memberClrProp?.GetCustomAttribute<ColumnAttribute>();

                    // Navigation / collection property
                    if (member.IsList)
                    {
                        var listElementType = member.ListElementType;
                        if (listElementType != null)
                        {
                            entityInfo.Relationships.Add(new RelationshipInfo
                            {
                                PropertyName = member.Name,
                                TargetEntity = listElementType.Name,
                                TargetClrType = listElementType,
                                IsCollection = true,
                            });
                        }
                        continue;
                    }

                    // Reference navigation (non-scalar whose type is a persistent type)
                    if (member.MemberTypeInfo?.IsPersistent == true)
                    {
                        entityInfo.Relationships.Add(new RelationshipInfo
                        {
                            PropertyName = member.Name,
                            TargetEntity = member.MemberTypeInfo.Name,
                            TargetClrType = member.MemberType,
                            IsCollection = false,
                        });
                        continue;
                    }

                    // Scalar property
                    var propInfo = new EntityPropertyInfo
                    {
                        Name = member.Name,
                        Description = memberAiDescription?.Description,
                        ColumnName = columnAttr?.Name ?? member.Name,
                        TypeName = GetFriendlyTypeName(member.MemberType),
                        ClrType = member.MemberType,
                        IsRequired = !IsNullableType(member.MemberType),
                    };

                    // Enum values
                    var underlyingType = Nullable.GetUnderlyingType(member.MemberType) ?? member.MemberType;
                    if (underlyingType.IsEnum)
                    {
                        propInfo.EnumValues = Enum.GetNames(underlyingType).ToList();
                    }

                    entityInfo.Properties.Add(propInfo);
                }

                entities.Add(entityInfo);
            }

            return new SchemaInfo { Entities = entities.OrderBy(e => e.Name).ToList() };
        }

        private static string GetFriendlyTypeName(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null)
                return GetFriendlyTypeName(underlying) + "?";

            if (type == typeof(string)) return "string";
            if (type == typeof(int)) return "int";
            if (type == typeof(long)) return "long";
            if (type == typeof(decimal)) return "decimal";
            if (type == typeof(double)) return "double";
            if (type == typeof(float)) return "float";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(DateTime)) return "DateTime";
            if (type == typeof(Guid)) return "Guid";
            if (type.IsEnum) return type.Name;
            return type.Name;
        }

        private static bool IsNullableType(Type type) =>
            !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
    }

    // ── Schema model ────────────────────────────────────────────────────

    public sealed class SchemaInfo
    {
        public List<EntityInfo> Entities { get; set; } = new();

        /// <summary>
        /// Finds an entity by name (case-insensitive).
        /// </summary>
        public EntityInfo FindEntity(string name) =>
            Entities.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public sealed class EntityInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string TableName { get; set; }
        public Type ClrType { get; set; }
        public List<EntityPropertyInfo> Properties { get; set; } = new();
        public List<RelationshipInfo> Relationships { get; set; } = new();
    }

    public sealed class EntityPropertyInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string ColumnName { get; set; }
        public string TypeName { get; set; }
        public Type ClrType { get; set; }
        public bool IsRequired { get; set; }
        public List<string> EnumValues { get; set; } = new();
    }

    public sealed class RelationshipInfo
    {
        public string PropertyName { get; set; }
        public string TargetEntity { get; set; }
        public Type TargetClrType { get; set; }
        public bool IsCollection { get; set; }
    }
}
