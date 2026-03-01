using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using XafTornado.Module.Attributes;

namespace XafTornado.Module.BusinessObjects
{
    [DefaultClassOptions]
    [NavigationItem("Geography")]
    [ImageName("BO_Country")]
    [DefaultProperty(nameof(Name))]
    [AIVisible]
    [AIDescription("Geographic regions containing territories")]
    public class Region : BaseObject
    {
        [StringLength(100)]
        public virtual string Name { get; set; }

        public virtual IList<Territory> Territories { get; set; } = new ObservableCollection<Territory>();
    }
}
