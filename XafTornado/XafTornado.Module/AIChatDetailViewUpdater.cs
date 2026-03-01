using DevExpress.ExpressApp.Model;
using DevExpress.ExpressApp.Model.Core;
using DevExpress.ExpressApp.Model.NodeGenerators;
using XafTornado.Module.Editors;

namespace XafTornado.Module
{
    /// <summary>
    /// Model updater that automatically adds the <c>AIChatViewItem</c>
    /// to the <c>AIChat_DetailView</c> and configures its layout so the
    /// chat control fills the entire view.
    /// </summary>
    public class AIChatDetailViewUpdater : ModelNodesGeneratorUpdater<ModelViewsNodesGenerator>
    {
        public override void UpdateNode(ModelNode node)
        {
            var views = (IModelViews)node;
            if (views["AIChat_DetailView"] is not IModelDetailView dv)
                return;

            // 1. Add the AIChatViewItem to the Items collection.
            const string chatItemId = "AIChatItem";
            if (dv.Items[chatItemId] == null)
            {
                dv.Items.AddNode<IModelAIChatViewItem>(chatItemId);
            }

            // 2. Remove the Oid property editor — not useful for the chat view.
            var oidItem = dv.Items["Oid"];
            if (oidItem != null)
                ((IModelNode)oidItem).Remove();

            // 3. Rebuild the layout so only the chat item is displayed.
            var layout = dv.Layout;
            if (layout == null)
                return;

            // Clear any auto-generated layout entries.
            for (int i = layout.Count - 1; i >= 0; i--)
            {
                layout[i].Remove();
            }

            // Add a single layout entry for the chat ViewItem.
            var chatLayoutItem = layout.AddNode<IModelLayoutViewItem>(chatItemId);
            chatLayoutItem.ViewItem = (IModelViewItem)dv.Items[chatItemId];
        }
    }
}
