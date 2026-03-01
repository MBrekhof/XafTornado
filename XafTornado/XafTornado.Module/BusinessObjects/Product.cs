using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using XafTornado.Module.Attributes;

namespace XafTornado.Module.BusinessObjects
{
    [DefaultClassOptions]
    [NavigationItem("Catalog")]
    [ImageName("BO_Product")]
    [DefaultProperty(nameof(Name))]
    [AIVisible]
    [AIDescription("Products in the catalog with pricing, stock levels, and supplier information")]
    public class Product : BaseObject
    {
        [StringLength(128)]
        public virtual string Name { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public virtual decimal UnitPrice { get; set; }

        public virtual int UnitsInStock { get; set; }

        public virtual bool Discontinued { get; set; }

        public virtual Guid? CategoryId { get; set; }

        [ForeignKey(nameof(CategoryId))]
        public virtual Category Category { get; set; }

        public virtual Guid? SupplierId { get; set; }

        [ForeignKey(nameof(SupplierId))]
        public virtual Supplier Supplier { get; set; }

        public virtual IList<OrderItem> OrderItems { get; set; } = new ObservableCollection<OrderItem>();
    }
}
