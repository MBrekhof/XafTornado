using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using XafTornado.Module.Attributes;

namespace XafTornado.Module.BusinessObjects
{
    [DefaultClassOptions]
    [NavigationItem("Catalog")]
    [ImageName("BO_Vendor")]
    [DefaultProperty(nameof(CompanyName))]
    [AIVisible]
    [AIDescription("Product suppliers with contact and location details")]
    public class Supplier : BaseObject
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

        public virtual IList<Product> Products { get; set; } = new ObservableCollection<Product>();
    }
}
