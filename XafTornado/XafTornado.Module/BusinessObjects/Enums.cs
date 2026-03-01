using DevExpress.Persistent.Base;

namespace XafTornado.Module.BusinessObjects
{
    public enum OrderStatus
    {
        New = 0,
        Processing = 1,
        Shipped = 2,
        Delivered = 3,
        Cancelled = 4
    }

    public enum InvoiceStatus
    {
        Draft = 0,
        Sent = 1,
        Paid = 2,
        Overdue = 3,
        Cancelled = 4
    }
}
