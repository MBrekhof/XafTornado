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
    [ImageName("BO_Employee")]
    [DefaultProperty(nameof(FullName))]
    [AIVisible]
    [AIDescription("Staff members with department and territory assignments")]
    public class Employee : BaseObject
    {
        [StringLength(64)]
        public virtual string FirstName { get; set; }

        [StringLength(64)]
        public virtual string LastName { get; set; }

        [StringLength(128)]
        public virtual string Title { get; set; }

        public virtual DateTime? HireDate { get; set; }

        [StringLength(128)]
        public virtual string Email { get; set; }

        [StringLength(32)]
        public virtual string Phone { get; set; }

        [AIVisible(false)]
        public virtual Guid? ReportsToId { get; set; }

        [ForeignKey(nameof(ReportsToId))]
        public virtual Employee ReportsTo { get; set; }

        public virtual IList<Employee> DirectReports { get; set; } = new ObservableCollection<Employee>();

        public virtual IList<EmployeeTerritory> Territories { get; set; } = new ObservableCollection<EmployeeTerritory>();

        public virtual IList<Order> Orders { get; set; } = new ObservableCollection<Order>();

        [AIVisible(false)]
        public virtual Guid? DepartmentId { get; set; }

        [ForeignKey(nameof(DepartmentId))]
        public virtual Department Department { get; set; }

        public string FullName => string.Join(" ", new[] { FirstName, LastName }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}
