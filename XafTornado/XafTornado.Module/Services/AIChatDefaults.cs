using System.Collections.Generic;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Ganss.Xss;

namespace XafTornado.Module.Services
{
    /// <summary>
    /// Single source of truth for AI chat UI configuration shared
    /// across Blazor and WinForms platform projects.
    /// </summary>
    public static class AIChatDefaults
    {
        // ── Header / Empty State ──────────────────────────────────────────

        public const string HeaderText = "AI Assistant";

        public const string EmptyStateText =
            "Ask me anything about your data — orders, invoices, products, employees & more.\nPowered by LLMTornado.";

        // ── Prompt Suggestions ────────────────────────────────────────────

        /// <summary>
        /// Lightweight DTO used by both platforms to build their
        /// native suggestion controls.
        /// </summary>
        public record PromptSuggestionItem(string Title, string Text, string Prompt);

        public static IReadOnlyList<PromptSuggestionItem> PromptSuggestions { get; } = new List<PromptSuggestionItem>
        {
            // Use Case 1 — Natural-Language Order Lookup
            new("Order Lookup",
                "Search orders by customer name or status",
                "Show me all orders for Around the Horn that are still processing"),

            // Use Case 2 — Invoice Aging Report
            new("Invoice Aging",
                "Overdue invoices grouped by customer",
                "Give me an aging summary of overdue invoices grouped by customer, including totals and recommendations"),

            // Use Case 3 — Product Restock Advisor
            new("Restock Advisor",
                "Low-stock products with supplier contacts",
                "Which products have fewer than 20 units in stock and are not discontinued? Include the supplier name and contact info"),

            // Use Case 4 — Employee Performance
            new("Sales Leaderboard",
                "Employee order stats and territory coverage",
                "How are the sales reps performing? Rank them by number of orders and show how many territories each one covers"),

            // Use Case 5 — Create an Order
            new("Create Order",
                "Conversational order entry via AI",
                "Create a new order for customer Alfreds Futterkiste: 10 units of Chai and 5 units of Chang, ship via Speedy Express")
        };

        // ── System Prompt ─────────────────────────────────────────────────

        /// <summary>
        /// Builds the system prompt dynamically from the XAF model metadata
        /// discovered by <see cref="SchemaDiscoveryService"/>.
        /// </summary>
        public static string GetSystemPrompt(SchemaDiscoveryService schemaService)
            => schemaService.GenerateSystemPrompt();

        // ── Markdown → HTML ───────────────────────────────────────────────

        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseEmphasisExtras()
            .UseAutoLinks()
            .UseTaskLists()
            .Build();

        private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

        private static HtmlSanitizer CreateSanitizer()
        {
            var sanitizer = new HtmlSanitizer();
            // Ensure table tags survive sanitization
            foreach (var tag in new[] { "table", "thead", "tbody", "tr", "th", "td" })
                sanitizer.AllowedTags.Add(tag);
            return sanitizer;
        }

        /// <summary>
        /// Converts a Markdown string to sanitized HTML.
        /// Thread-safe — the pipeline and sanitizer instances are reentrant.
        /// </summary>
        public static string ConvertMarkdownToHtml(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
                return string.Empty;

            var html = Markdown.ToHtml(markdown, Pipeline);
            return Sanitizer.Sanitize(html);
        }
    }
}
