using System;

namespace XafTornado.Module.Attributes
{
    /// <summary>
    /// Provides a human-readable description of an entity or property for AI integrations.
    /// The description is included in system prompts and tool output to help the AI
    /// understand the purpose of each element.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class AIDescriptionAttribute : Attribute
    {
        public string Description { get; }

        public AIDescriptionAttribute(string description)
        {
            Description = description ?? throw new ArgumentNullException(nameof(description));
        }
    }
}
