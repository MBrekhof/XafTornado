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
    [ImageName("BO_Shipment")]
    [DefaultProperty(nameof(CompanyName))]
    [AIVisible]
    [AIDescription("Shipping companies that deliver orders")]
    public class Shipper : BaseObject
    {
        [StringLength(128)]
        public virtual string CompanyName { get; set; }

        [StringLength(32)]
        public virtual string Phone { get; set; }

        public virtual IList<Order> Orders { get; set; } = new ObservableCollection<Order>();
    }
}
