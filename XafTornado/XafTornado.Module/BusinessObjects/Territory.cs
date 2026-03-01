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
    [NavigationItem("Geography")]
    [ImageName("BO_Position")]
    [DefaultProperty(nameof(Name))]
    [AIVisible]
    [AIDescription("Sales territories within regions, assigned to employees")]
    public class Territory : BaseObject
    {
        [StringLength(100)]
        public virtual string Name { get; set; }

        public virtual Guid? RegionId { get; set; }

        [ForeignKey(nameof(RegionId))]
        public virtual Region Region { get; set; }

        public virtual IList<EmployeeTerritory> EmployeeTerritories { get; set; } = new ObservableCollection<EmployeeTerritory>();
    }
}
