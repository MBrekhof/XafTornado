using System.ComponentModel;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;

namespace XafTornado.Module.BusinessObjects
{
    /// <summary>
    /// Non-persistent object that serves as the data source for the AI Chat Detail View.
    /// The Detail View layout will contain a <c>AIChatViewItem</c> that displays
    /// the DevExpress AI Chat control, wired to the GitHub AI SDK.
    /// </summary>
    [DomainComponent]
    [DefaultClassOptions]
    [DefaultProperty(nameof(Caption))]
    [ImageName("Actions_EnterGroup")]
    public class AIChat : NonPersistentBaseObject
    {
        public AIChat()
        {
            Caption = "AI Assistant";
        }

        [Browsable(false)]
        public string Caption { get; set; }
    }
}
