using System;
using System.Threading;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Blazor;
using DevExpress.ExpressApp.Editors;
using DevExpress.ExpressApp.Model;
using Microsoft.AspNetCore.Components;
using XafTornado.Module.Editors;

namespace XafTornado.Blazor.Server.Editors.AIChatViewItem
{
    /// <summary>
    /// Blazor ViewItem that hosts the DevExpress <c>DxAIChat</c> component.
    /// Messages are routed automatically through the registered <c>IChatClient</c>
    /// (backed by the GitHub AI SDK).
    /// </summary>
    [ViewItem(typeof(IModelAIChatViewItem))]
    public class AIChatViewItemBlazor : ViewItem, IComponentContentHolder
    {
        public AIChatViewItemBlazor(IModelViewItem model, Type objectType)
            : base(objectType, model.Id)
        {
        }

        RenderFragment IComponentContentHolder.ComponentContent => builder =>
        {
            builder.OpenComponent<AIChat>(0);
            builder.CloseComponent();
        };

        protected override object CreateControlCore()
        {
            // In Blazor, IComponentContentHolder.ComponentContent is used for rendering.
            // Return a placeholder object to satisfy the ViewItem contract.
            return new object();
        }
    }
}
