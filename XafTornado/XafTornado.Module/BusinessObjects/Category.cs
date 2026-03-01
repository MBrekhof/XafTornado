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
    [ImageName("BO_Category")]
    [DefaultProperty(nameof(Name))]
    [AIVisible]
    [AIDescription("Product categories for organizing the catalog")]
    public class Category : BaseObject
    {
        [StringLength(128)]
        public virtual string Name { get; set; }

        [StringLength(512)]
        public virtual string Description { get; set; }

        public virtual IList<Product> Products { get; set; } = new ObservableCollection<Product>();
    }
}
