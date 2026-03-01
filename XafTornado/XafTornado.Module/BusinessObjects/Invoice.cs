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
    [ImageName("BO_Invoice")]
    [DefaultProperty(nameof(InvoiceNumber))]
    [AIVisible]
    [AIDescription("Invoices for billing with status tracking and order associations")]
    public class Invoice : BaseObject
    {
        [StringLength(32)]
        public virtual string InvoiceNumber { get; set; }

        public virtual DateTime InvoiceDate { get; set; }

        public virtual DateTime? DueDate { get; set; }

        public virtual InvoiceStatus Status { get; set; }

        public virtual IList<Order> Orders { get; set; } = new ObservableCollection<Order>();

        [NotMapped]
        public decimal TotalAmount
        {
            get
            {
                decimal total = 0m;
                foreach (var order in Orders)
                {
                    if (order?.OrderItems == null)
                    {
                        continue;
                    }
                    foreach (var item in order.OrderItems)
                    {
                        var line = item.UnitPrice * item.Quantity;
                        var discountFactor = 1m - (item.Discount / 100m);
                        total += line * discountFactor;
                    }
                }
                return total;
            }
        }
    }
}
