using System.Configuration;
using DevExpress.EntityFrameworkCore.Security;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.ApplicationBuilder;
using DevExpress.ExpressApp.Design;
using DevExpress.ExpressApp.EFCore;
using DevExpress.ExpressApp.Security;
using DevExpress.ExpressApp.Win;
using DevExpress.ExpressApp.Win.ApplicationBuilder;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using DevExpress.XtraEditors;
using Microsoft.EntityFrameworkCore;
using DevExpress.AIIntegration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XafTornado.Module.Services;
using XafTornado.Win.Services;

namespace XafTornado.Win
{
    public class ApplicationBuilder : IDesignTimeApplicationFactory
    {
        public static WinApplication BuildApplication(string connectionString)
        {
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
            // Build configuration from appsettings.json so we can register AIChatService.
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
                .Build();

            var builder = WinApplication.CreateBuilder();
            // Register logging (required by AIChatService).
            builder.Services.AddLogging();
            // Register the LLMTornado chat service and AI tools for DI.
            builder.Services.AddAIServices(configuration);
            // Enable DevExpress AI infrastructure (required by AIChatControl).
            builder.Services.AddDevExpressAI();
            // Register WinForms navigation service so AI tools can navigate/filter/toggle the side panel.
            builder.Services.AddSingleton<WinNavigationService>();
            builder.Services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<WinNavigationService>());

            // Register 3rd-party IoC containers (like Autofac, Dryloc, etc.)
            // builder.UseServiceProviderFactory(new DryIocServiceProviderFactory());
            // builder.UseServiceProviderFactory(new AutofacServiceProviderFactory());

            builder.UseApplication<XafTornadoWindowsFormsApplication>();
            builder.Modules
                .AddCloning()
                .AddConditionalAppearance()
                .AddFileAttachments()
                .AddReports(options =>
                {
                    options.EnableInplaceReports = true;
                    options.ReportDataType = typeof(DevExpress.Persistent.BaseImpl.EF.ReportDataV2);
                    options.ReportStoreMode = DevExpress.ExpressApp.ReportsV2.ReportStoreModes.XML;
                })
                .AddValidation(options =>
                {
                    options.AllowValidationDetailsAccess = false;
                })
                .Add<XafTornado.Module.XafTornadoModule>()
                .Add<XafTornadoWinModule>();
            builder.ObjectSpaceProviders
                .AddSecuredEFCore(options =>
                {
                    options.PreFetchReferenceProperties();
                })
                    .WithDbContext<XafTornado.Module.BusinessObjects.XafTornadoEFCoreDbContext>((application, options) =>
                    {
                        // Uncomment this code to use an in-memory database. This database is recreated each time the server starts. With the in-memory database, you don't need to make a migration when the data model is changed.
                        // Do not use this code in production environment to avoid data loss.
                        // We recommend that you refer to the following help topic before you use an in-memory database: https://docs.microsoft.com/en-us/ef/core/testing/in-memory
                        //options.UseInMemoryDatabase();
                        options.UseConnectionString(connectionString);
                    })
                .AddNonPersistent();
            builder.Security
                .UseIntegratedMode(options =>
                {
                    options.Lockout.Enabled = true;

                    options.RoleType = typeof(PermissionPolicyRole);
                    options.UserType = typeof(XafTornado.Module.BusinessObjects.ApplicationUser);
                    options.UserLoginInfoType = typeof(XafTornado.Module.BusinessObjects.ApplicationUserLoginInfo);
                    options.Events.OnSecurityStrategyCreated += securityStrategy =>
                    {
                        // Use the 'PermissionsReloadMode.NoCache' option to load the most recent permissions from the database once
                        // for every DbContext instance when secured data is accessed through this instance for the first time.
                        // Use the 'PermissionsReloadMode.CacheOnFirstAccess' option to reduce the number of database queries.
                        // In this case, permission requests are loaded and cached when secured data is accessed for the first time
                        // and used until the current user logs out.
                        // See the following article for more details: https://docs.devexpress.com/eXpressAppFramework/DevExpress.ExpressApp.Security.SecurityStrategy.PermissionsReloadMode.
                        ((SecurityStrategy)securityStrategy).PermissionsReloadMode = PermissionsReloadMode.NoCache;
                    };
                })
                .AddPasswordAuthentication();
            builder.AddBuildStep(application =>
            {
                application.ConnectionString = connectionString;
#if DEBUG
                if(System.Diagnostics.Debugger.IsAttached && application.CheckCompatibilityType == CheckCompatibilityType.DatabaseSchema) {
                    application.DatabaseUpdateMode = DatabaseUpdateMode.UpdateDatabaseAlways;
                }
#endif
            });
            var winApplication = builder.Build();

            // Wire AI tools and register with DevExpress desktop AI container.
            try
            {
                var service = winApplication.ServiceProvider.GetRequiredService<AIChatService>();
                var toolsProvider = winApplication.ServiceProvider.GetRequiredService<AIToolsProvider>();
                var schemaService = winApplication.ServiceProvider.GetRequiredService<SchemaDiscoveryService>();

                service.ToolFunctions = toolsProvider.Tools;
                service.TornadoTools = toolsProvider.GetTornadoTools();
                service.SystemMessage = schemaService.GenerateSystemPrompt();

                var chatClient = new AIChatClient(service);
                AIExtensionsContainerDesktop.Default.RegisterChatClient(chatClient);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AI tools not available: {ex.Message}");
            }

            return winApplication;
        }

        XafApplication IDesignTimeApplicationFactory.Create()
            => BuildApplication(XafApplication.DesignTimeConnectionString);
    }
}
