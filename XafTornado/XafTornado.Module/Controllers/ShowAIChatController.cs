using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.ExpressApp.SystemModule;
using DevExpress.Persistent.Base;
using Microsoft.Extensions.DependencyInjection;
using XafTornado.Module.BusinessObjects;
using XafTornado.Module.Services;

namespace XafTornado.Module.Controllers
{
    /// <summary>
    /// Window controller that:
    /// 1. Intercepts navigation to <c>AIChat_ListView</c> and redirects to the side panel (Blazor)
    ///    or DetailView (WinForms).
    /// 2. Provides a "AI Chat" action in the View menu.
    /// </summary>
    public class ShowAIChatController : WindowController
    {
        private SimpleAction _showAIChatAction;

        public ShowAIChatController()
        {
            TargetWindowType = WindowType.Main;

            _showAIChatAction = new SimpleAction(this, "ShowAIChat", PredefinedCategory.View)
            {
                Caption = "AI Assistant",
                ImageName = "Actions_EnterGroup",
                ToolTip = "Toggle the AI assistant panel"
            };
            _showAIChatAction.Execute += ShowAIChatAction_Execute;
        }

        protected override void OnActivated()
        {
            base.OnActivated();
            var navController = Frame.GetController<ShowNavigationItemController>();
            if (navController != null)
            {
                navController.CustomShowNavigationItem += OnCustomShowNavigationItem;
            }
        }

        protected override void OnDeactivated()
        {
            var navController = Frame.GetController<ShowNavigationItemController>();
            if (navController != null)
            {
                navController.CustomShowNavigationItem -= OnCustomShowNavigationItem;
            }
            base.OnDeactivated();
        }

        private void OnCustomShowNavigationItem(object sender, CustomShowNavigationItemEventArgs e)
        {
            if (e.ActionArguments.SelectedChoiceActionItem?.Data is ViewShortcut shortcut
                && shortcut.ViewId == "AIChat_ListView")
            {
                // On Blazor, toggle the side panel. On WinForms, open a DetailView.
                if (TryToggleSidePanel())
                {
                    e.Handled = true;
                }
                else
                {
                    OpenAIChat(e.ActionArguments.ShowViewParameters);
                    e.Handled = true;
                }
            }
        }

        private void ShowAIChatAction_Execute(object sender, SimpleActionExecuteEventArgs e)
        {
            if (!TryToggleSidePanel())
            {
                OpenAIChat(e.ShowViewParameters);
            }
        }

        /// <summary>
        /// Attempts to toggle the side panel via <see cref="INavigationService"/>.
        /// Returns true if a navigation service is available (Blazor), false otherwise (WinForms).
        /// </summary>
        private bool TryToggleSidePanel()
        {
            var navService = Application.ServiceProvider.GetService<INavigationService>();
            if (navService != null)
            {
                navService.ToggleSidePanel();
                return true;
            }
            return false;
        }

        private void OpenAIChat(ShowViewParameters showViewParameters)
        {
            var objectSpace = Application.CreateObjectSpace(typeof(AIChat));
            var chatObject = objectSpace.CreateObject<AIChat>();
            var detailView = Application.CreateDetailView(objectSpace, chatObject);
            detailView.ViewEditMode = DevExpress.ExpressApp.Editors.ViewEditMode.View;
            showViewParameters.CreatedView = detailView;
        }
    }
}
