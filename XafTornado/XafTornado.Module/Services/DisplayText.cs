using System;
using System.Collections.Generic;
using System.Linq;

namespace XafTornado.Module.Services
{
    /// <summary>
    /// Human-readable label for an entity object, and name-based record resolution shared by the
    /// AI tools and the navigation executors.
    /// </summary>
    public static class DisplayText
    {
        private static readonly string[] LabelProperties =
            { "Name", "CompanyName", "FullName", "FirstName", "Title", "InvoiceNumber", "Description" };

        /// <summary>The first non-null value among the common "name" properties, else <c>ToString()</c>.</summary>
        public static string Of(object obj)
        {
            if (obj == null) return null;
            var type = obj.GetType();
            foreach (var propName in LabelProperties)
            {
                var prop = type.GetProperty(propName);
                if (prop == null) continue;
                var val = prop.GetValue(obj);
                if (val != null) return val.ToString();
            }
            return obj.ToString();
        }

        /// <summary>
        /// Resolves <paramref name="term"/> against the display text of <paramref name="items"/>.
        /// An exact (case-insensitive) match wins; otherwise a single substring match wins.
        /// <c>Match</c> is null when nothing matched (<c>Candidates</c> empty) or when the term is
        /// ambiguous (<c>Candidates</c> has more than one entry). Never picks an arbitrary hit (AI-006).
        /// </summary>
        public static (object Match, List<object> Candidates) Resolve(IEnumerable<object> items, string term)
        {
            var list = items as List<object> ?? items.ToList();

            var exact = list.Where(o => string.Equals(Of(o), term, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0)
                return (exact.Count == 1 ? exact[0] : null, exact);

            var partial = list.Where(o => Of(o)?.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            return (partial.Count == 1 ? partial[0] : null, partial);
        }
    }
}
