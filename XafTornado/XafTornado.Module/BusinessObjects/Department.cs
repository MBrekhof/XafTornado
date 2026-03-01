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
    [NavigationItem("HR")]
    [ImageName("BO_Department")]
    [DefaultProperty(nameof(Name))]
    [AIVisible]
    [AIDescription("Company departments with budget tracking and employee assignments")]
    public class Department : BaseObject
    {
        [StringLength(128)]
        public virtual string Name { get; set; }

        [StringLength(64)]
        public virtual string Code { get; set; }

        [StringLength(256)]
        public virtual string Location { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        [AIDescription("Annual operating budget in USD")]
        public virtual decimal Budget { get; set; }

        public virtual bool IsActive { get; set; } = true;

        public virtual IList<Employee> Employees { get; set; } = new ObservableCollection<Employee>();
    }
}
