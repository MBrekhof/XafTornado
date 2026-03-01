using DevExpress.ExpressApp.Model;

namespace XafTornado.Module.Editors
{
    /// <summary>
    /// Application Model interface for the AI Chat ViewItem.
    /// Both WinForms and Blazor platform-specific ViewItem classes
    /// reference this interface via <see cref="DevExpress.ExpressApp.Editors.ViewItemAttribute"/>
    /// so that a single model node name is shared across platforms.
    /// </summary>
    public interface IModelAIChatViewItem : IModelViewItem { }
}
