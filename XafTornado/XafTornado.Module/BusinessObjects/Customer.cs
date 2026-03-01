using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using XafTornado.Module.Attributes;

namespace XafTornado.Module.BusinessObjects
{
    [DefaultClassOptions]
    [NavigationItem("Sales")]
    [ImageName("BO_Customer")]
    [DefaultProperty(nameof(CompanyName))]
    [AIVisible]
    [AIDescription("Customers with contact information and order history")]
    public class Customer : BaseObject
    {
        [StringLength(128)]
        public virtual string CompanyName { get; set; }

        [StringLength(128)]
        public virtual string ContactName { get; set; }

        [StringLength(32)]
        public virtual string Phone { get; set; }

        [StringLength(128)]
        public virtual string Email { get; set; }

        [StringLength(256)]
        public virtual string Address { get; set; }

        [StringLength(64)]
        public virtual string City { get; set; }

        [StringLength(64)]
        public virtual string Country { get; set; }

        public virtual IList<Order> Orders { get; set; } = new ObservableCollection<Order>();
    }
}
