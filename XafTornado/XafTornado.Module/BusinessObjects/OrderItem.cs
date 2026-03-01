using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using XafTornado.Module.Attributes;

namespace XafTornado.Module.BusinessObjects
{
    [DefaultClassOptions]
    [NavigationItem("Sales")]
    [ImageName("BO_OrderItem")]
    [DefaultProperty(nameof(Quantity))]
    [AIVisible]
    [AIDescription("Line items within an order linking products to quantities and pricing")]
    public class OrderItem : BaseObject
    {
        [Column(TypeName = "decimal(18,2)")]
        public virtual decimal UnitPrice { get; set; }

        public virtual int Quantity { get; set; }

        [Column(TypeName = "decimal(5,2)")]
        public virtual decimal Discount { get; set; }

        public virtual Guid? OrderId { get; set; }

        [ForeignKey(nameof(OrderId))]
        public virtual Order Order { get; set; }

        public virtual Guid? ProductId { get; set; }

        [ForeignKey(nameof(ProductId))]
        public virtual Product Product { get; set; }
    }
}
