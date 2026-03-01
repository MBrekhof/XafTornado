using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.ConditionalAppearance;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafTornado.Module.BusinessObjects
{
    [Table("PermissionPolicyUserLoginInfo")]
    public class ApplicationUserLoginInfo : BaseObject, ISecurityUserLoginInfo
    {
        public ApplicationUserLoginInfo() { }

        [Appearance("PasswordProvider", Enabled = false, Criteria = "!(IsNewObject(this)) and LoginProviderName == '" + SecurityDefaults.PasswordAuthentication + "'", Context = "DetailView")]
        public virtual string LoginProviderName { get; set; }

        [Appearance("PasswordProviderUserKey", Enabled = false, Criteria = "!(IsNewObject(this)) and LoginProviderName == '" + SecurityDefaults.PasswordAuthentication + "'", Context = "DetailView")]
        public virtual string ProviderUserKey { get; set; }

        [Browsable(false)]
        public virtual Guid UserForeignKey { get; set; }

        [Required]
        [ForeignKey(nameof(UserForeignKey))]
        public virtual ApplicationUser User { get; set; }

        object ISecurityUserLoginInfo.User => User;
    }
}
