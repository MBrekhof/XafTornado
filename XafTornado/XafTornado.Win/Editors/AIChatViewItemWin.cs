using System;
using System.Linq;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Editors;
using DevExpress.ExpressApp.Model;
using DevExpress.Utils;
using DevExpress.AIIntegration.Blazor.Chat;
using DevExpress.AIIntegration.Blazor.Chat.WebView;
using DevExpress.AIIntegration.WinForms.Chat;
using Microsoft.Extensions.AI;
using XafTornado.Module.Editors;
using XafTornado.Module.Services;

namespace XafTornado.Win.Editors
{
    /// <summary>
    /// WinForms ViewItem that hosts the DevExpress <see cref="AIChatControl"/>.
    /// Messages are routed automatically through the registered <c>IChatClient</c>
    /// (backed by the GitHub AI SDK).
    /// </summary>
    [ViewItem(typeof(IModelAIChatViewItem))]
    public class AIChatViewItemWin : ViewItem
    {
        private AIChatControl _chatControl;

        public AIChatViewItemWin(IModelViewItem model, Type objectType)
            : base(objectType, model.Id)
        {
        }

        protected override object CreateControlCore()
        {
            _chatControl = new AIChatControl
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                UseStreaming = DefaultBoolean.True,
                ShowHeader = DefaultBoolean.True,
                HeaderText = AIChatDefaults.HeaderText,
                EmptyStateText = AIChatDefaults.EmptyStateText,
                ContentFormat = ResponseContentFormat.Markdown
            };

            // Markdown rendering via shared helper
            _chatControl.MarkdownConvert += OnMarkdownConvert;

            // Prompt suggestions from centralized definitions
            _chatControl.SetPromptSuggestions(
                AIChatDefaults.PromptSuggestions
                    .Select(s => new PromptSuggestion(title: s.Title, text: s.Text, prompt: s.Prompt))
                    .ToList());

            // System prompt is set on AIChatService via SchemaDiscoveryService (no UI-level injection needed).

            return _chatControl;
        }

        private void OnMarkdownConvert(object sender, AIChatControlMarkdownConvertEventArgs e)
        {
            var html = AIChatDefaults.ConvertMarkdownToHtml(e.MarkdownText);
            e.HtmlText = (Microsoft.AspNetCore.Components.MarkupString)html;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _chatControl != null)
            {
                _chatControl.MarkdownConvert -= OnMarkdownConvert;
                _chatControl.Dispose();
                _chatControl = null;
            }
            base.Dispose(disposing);
        }
    }
}
