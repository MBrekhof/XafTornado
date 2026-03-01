using System;

namespace XafTornado.Module.Attributes
{
    /// <summary>
    /// Controls whether an entity or property is discoverable by AI integrations.
    /// <para>
    /// On a class: opts the entity into AI discovery (when any entity in the model
    /// has this attribute, only attributed entities are discovered).
    /// </para>
    /// <para>
    /// On a property with <c>false</c>: explicitly excludes the property even when
    /// the entity is visible.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class AIVisibleAttribute : Attribute
    {
        public bool IsVisible { get; }

        public AIVisibleAttribute(bool isVisible = true)
        {
            IsVisible = isVisible;
        }
    }
}
