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
    [NavigationItem("Sales")]
    [ImageName("BO_Order")]
    [DefaultProperty(nameof(OrderDate))]
    [AIVisible]
    [AIDescription("Customer orders with shipping and status tracking")]
    public class Order : BaseObject
    {
        public virtual DateTime OrderDate { get; set; }

        public virtual DateTime? RequiredDate { get; set; }

        public virtual DateTime? ShippedDate { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public virtual decimal Freight { get; set; }

        [StringLength(256)]
        [AIVisible(false)]
        public virtual string ShipAddress { get; set; }

        [StringLength(64)]
        public virtual string ShipCity { get; set; }

        [StringLength(64)]
        public virtual string ShipCountry { get; set; }

        public virtual OrderStatus Status { get; set; }

        public virtual Guid? CustomerId { get; set; }

        [ForeignKey(nameof(CustomerId))]
        public virtual Customer Customer { get; set; }

        public virtual Guid? EmployeeId { get; set; }

        [ForeignKey(nameof(EmployeeId))]
        public virtual Employee Employee { get; set; }

        public virtual Guid? ShipperId { get; set; }

        [ForeignKey(nameof(ShipperId))]
        public virtual Shipper Shipper { get; set; }

        public virtual Guid? InvoiceId { get; set; }

        [ForeignKey(nameof(InvoiceId))]
        public virtual Invoice Invoice { get; set; }

        public virtual IList<OrderItem> OrderItems { get; set; } = new ObservableCollection<OrderItem>();
    }
}
