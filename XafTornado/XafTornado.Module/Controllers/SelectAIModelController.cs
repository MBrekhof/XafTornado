using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.Persistent.Base;
using Microsoft.Extensions.DependencyInjection;
using XafTornado.Module.Services;

namespace XafTornado.Module.Controllers
{
    /// <summary>
    /// Window controller that provides a <see cref="SingleChoiceAction"/> to switch
    /// the LLMTornado model used by <see cref="AIChatService"/> at runtime.
    /// </summary>
    public class SelectAIModelController : WindowController
    {
        private SingleChoiceAction _selectModelAction;

        /// <summary>
        /// Models available through LLMTornado (multi-provider).
        /// Uses native provider model IDs.
        /// </summary>
        private static readonly string[] AvailableModels = new[]
        {
            // Anthropic
            "claude-sonnet-4-6",
            "claude-sonnet-4-5",
            "claude-opus-4-6",
            // OpenAI
            "gpt-4o",
            "gpt-4o-mini",
            "gpt-4.1",
            "gpt-4.1-mini",
            "o3-mini",
            "o4-mini",
            // Google
            "gemini-2.5-pro",
            "gemini-2.5-flash",
            // Mistral
            "mistral-large-latest",
        };

        public SelectAIModelController()
        {
            TargetWindowType = WindowType.Main;

            _selectModelAction = new SingleChoiceAction(this, "SelectAIModel", PredefinedCategory.View)
            {
                Caption = "AI Model",
                ImageName = "ModelEditor_Class",
                ToolTip = "Select the AI model for AI Chat",
                ItemType = SingleChoiceActionItemType.ItemIsMode,
            };

            foreach (var model in AvailableModels)
            {
                _selectModelAction.Items.Add(new ChoiceActionItem(model, model));
            }

            _selectModelAction.Execute += SelectModelAction_Execute;
        }

        protected override void OnActivated()
        {
            base.OnActivated();

            // Highlight the currently configured model.
            var service = Application.ServiceProvider.GetService<AIChatService>();
            if (service != null)
            {
                var currentModel = service.CurrentModel;
                var item = _selectModelAction.Items.FirstOrDefault(i => (string)i.Data == currentModel);
                if (item != null)
                {
                    _selectModelAction.SelectedItem = item;
                }
            }
        }

        private void SelectModelAction_Execute(object sender, SingleChoiceActionExecuteEventArgs e)
        {
            var selectedModel = (string)e.SelectedChoiceActionItem.Data;

            var service = Application.ServiceProvider.GetService<AIChatService>();
            if (service != null)
            {
                service.CurrentModel = selectedModel;
                service.ClearHistory(); // Reset conversation when switching models
                _selectModelAction.SelectedItem = e.SelectedChoiceActionItem;
            }
        }
    }
}
