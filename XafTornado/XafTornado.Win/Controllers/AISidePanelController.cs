using System;
using System.Linq;
using System.Windows.Forms;
using DevExpress.ExpressApp;
using DevExpress.AIIntegration.Blazor.Chat;
using DevExpress.AIIntegration.Blazor.Chat.WebView;
using DevExpress.AIIntegration.WinForms.Chat;
using DevExpress.Utils;
using DevExpress.XtraEditors;
using Microsoft.Extensions.DependencyInjection;
using XafTornado.Module.Services;
using XafTornado.Win.Services;

namespace XafTornado.Win.Controllers
{
    /// <summary>
    /// Adds a persistent AI chat side panel to the WinForms main window,
    /// mirroring the Blazor <c>AISidePanel</c> component.
    /// </summary>
    public class AISidePanelController : WindowController
    {
        private const int PanelWidth = 480;
        private PanelControl _sidePanel;
        private AIChatControl _chatControl;
        private Splitter _splitter;
        private WinNavigationService _navService;

        public AISidePanelController()
        {
            TargetWindowType = WindowType.Main;
        }

        protected override void OnActivated()
        {
            base.OnActivated();

            _navService = Application.ServiceProvider.GetService<INavigationService>() as WinNavigationService;
            if (_navService != null)
            {
                _navService.OnSidePanelToggleRequested += TogglePanel;
            }

            // Defer panel creation until the template (form) is ready.
            if (Window.Template is Form form)
            {
                CreateSidePanel(form);
            }
            else
            {
                Window.TemplateChanged += OnTemplateChanged;
            }
        }

        protected override void OnDeactivated()
        {
            if (_navService != null)
            {
                _navService.OnSidePanelToggleRequested -= TogglePanel;
                _navService = null;
            }

            Window.TemplateChanged -= OnTemplateChanged;
            DisposeSidePanel();
            base.OnDeactivated();
        }

        private void OnTemplateChanged(object sender, EventArgs e)
        {
            if (Window.Template is Form form)
            {
                Window.TemplateChanged -= OnTemplateChanged;
                CreateSidePanel(form);
            }
        }

        private void CreateSidePanel(Form form)
        {
            if (_sidePanel != null) return;

            _sidePanel = new PanelControl
            {
                Width = PanelWidth,
                Dock = DockStyle.Right,
                Visible = false,
                BorderStyle = DevExpress.XtraEditors.Controls.BorderStyles.NoBorder
            };

            _chatControl = new AIChatControl
            {
                Dock = DockStyle.Fill,
                UseStreaming = DefaultBoolean.True,
                ShowHeader = DefaultBoolean.True,
                HeaderText = AIChatDefaults.HeaderText,
                EmptyStateText = AIChatDefaults.EmptyStateText,
                ContentFormat = ResponseContentFormat.Markdown
            };

            _chatControl.MarkdownConvert += OnMarkdownConvert;
            _chatControl.SetPromptSuggestions(
                AIChatDefaults.PromptSuggestions
                    .Select(s => new PromptSuggestion(title: s.Title, text: s.Text, prompt: s.Prompt))
                    .ToList());

            _sidePanel.Controls.Add(_chatControl);

            _splitter = new Splitter
            {
                Dock = DockStyle.Right,
                Width = 4,
                Visible = false
            };

            // Add to form — DockStyle.Right panels dock in add-order.
            form.Controls.Add(_sidePanel);
            form.Controls.Add(_splitter);
        }

        private void OnMarkdownConvert(object sender, AIChatControlMarkdownConvertEventArgs e)
        {
            e.HtmlText = (Microsoft.AspNetCore.Components.MarkupString)AIChatDefaults.ConvertMarkdownToHtml(e.MarkdownText);
        }

        public void TogglePanel()
        {
            if (_sidePanel == null) return;

            var visible = !_sidePanel.Visible;
            _sidePanel.Visible = visible;
            _splitter.Visible = visible;
        }

        private void DisposeSidePanel()
        {
            if (_chatControl != null)
            {
                _chatControl.MarkdownConvert -= OnMarkdownConvert;
                _chatControl.Dispose();
                _chatControl = null;
            }

            _splitter?.Dispose();
            _splitter = null;

            _sidePanel?.Dispose();
            _sidePanel = null;
        }
    }
}
