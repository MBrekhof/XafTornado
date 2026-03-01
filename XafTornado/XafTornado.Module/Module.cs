using System.ComponentModel;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Editors;
using DevExpress.ExpressApp.Model;
using DevExpress.ExpressApp.Model.Core;
using DevExpress.ExpressApp.Model.DomainLogics;
using DevExpress.ExpressApp.Model.NodeGenerators;
using DevExpress.ExpressApp.ReportsV2;
using DevExpress.ExpressApp.Updating;
using DevExpress.Persistent.Base;

namespace XafTornado.Module
{
    // For more typical usage scenarios, be sure to check out https://docs.devexpress.com/eXpressAppFramework/DevExpress.ExpressApp.ModuleBase.
    public sealed class XafTornadoModule : ModuleBase
    {
        public XafTornadoModule()
        {
            //
            // XafTornadoModule
            //
            AdditionalExportedTypes.Add(typeof(XafTornado.Module.BusinessObjects.ApplicationUser));
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.EF.PermissionPolicy.PermissionPolicyRole));
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.EF.ModelDifference));
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.EF.ModelDifferenceAspect));
            AdditionalExportedTypes.Add(typeof(XafTornado.Module.BusinessObjects.Category));
            AdditionalExportedTypes.Add(typeof(XafTornado.Module.BusinessObjects.Customer));
            AdditionalExportedTypes.Add(typeof(XafTornado.Module.BusinessObjects.Employee));
            AdditionalExportedTypes.Add(typeof(XafTornado.Module.BusinessObjects.EmployeeTerritory));
            AdditionalExportedTypes.Add(typeof(XafTornado.Module.BusinessObjects.Invoice));
            AdditionalExportedTypes.Add(typeof(XafTornado.Module.BusinessObjects.Order));
            AdditionalExportedTypes.Add(typeof(XafTornado.Module.BusinessObjects.OrderItem));
            AdditionalExportedTypes.Add(typeof(XafTornado.Module.BusinessObjects.Product));
            AdditionalExportedTypes.Add(typeof(XafTornado.Module.BusinessObjects.Region));
            AdditionalExportedTypes.Add(typeof(XafTornado.Module.BusinessObjects.Shipper));
            AdditionalExportedTypes.Add(typeof(XafTornado.Module.BusinessObjects.Supplier));
            AdditionalExportedTypes.Add(typeof(XafTornado.Module.BusinessObjects.Territory));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.SystemModule.SystemModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.Security.SecurityModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.CloneObject.CloneObjectModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.ConditionalAppearance.ConditionalAppearanceModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.ReportsV2.ReportsModuleV2));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.Validation.ValidationModule));
            DevExpress.ExpressApp.Security.SecurityModule.UsedExportedTypes = DevExpress.Persistent.Base.UsedExportedTypes.Custom;
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.EF.FileData));
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.EF.FileAttachment));
        }
        public override IEnumerable<ModuleUpdater> GetModuleUpdaters(IObjectSpace objectSpace, Version versionFromDB)
        {
            ModuleUpdater updater = new DatabaseUpdate.Updater(objectSpace, versionFromDB);
            return new ModuleUpdater[] { updater };
        }
        public override void Setup(XafApplication application)
        {
            base.Setup(application);
            // Manage various aspects of the application UI and behavior at the module level.
        }
        public override void AddGeneratorUpdaters(ModelNodesGeneratorUpdaters updaters)
        {
            base.AddGeneratorUpdaters(updaters);
            updaters.Add(new AIChatDetailViewUpdater());
        }
        public override void Setup(ApplicationModulesManager moduleManager)
        {
            base.Setup(moduleManager);
        }
    }
}
