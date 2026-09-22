using System.Configuration;
using System.Reflection;
using DevExpress.EntityFrameworkCore.Security;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.ApplicationBuilder;
using DevExpress.ExpressApp.EFCore;
using DevExpress.ExpressApp.Security;
using DevExpress.ExpressApp.Utils;
using DevExpress.ExpressApp.Win;
using DevExpress.ExpressApp.Win.ApplicationBuilder;
using DevExpress.ExpressApp.Win.Utils;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using DevExpress.XtraEditors;
using DevExpress.AIIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using XafTornado.Module.Services;

namespace XafTornado.Win
{
    internal static class Program
    {
        static bool ContainsArgument(string[] args, string argument)
        {
            return args.Any(arg => arg.TrimStart('/').TrimStart('-').ToLower() == argument.ToLower());
        }
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            if (ContainsArgument(args, "help") || ContainsArgument(args, "h"))
            {
                Console.WriteLine("Updates the database when its version does not match the application's version.");
                Console.WriteLine();
                Console.WriteLine($"    {Assembly.GetExecutingAssembly().GetName().Name}.exe --updateDatabase [--forceUpdate --silent]");
                Console.WriteLine();
                Console.WriteLine("--forceUpdate - Marks that the database must be updated whether its version matches the application's version or not.");
                Console.WriteLine("--silent - Marks that database update proceeds automatically and does not require any interaction with the user.");
                Console.WriteLine();
                Console.WriteLine($"Exit codes: 0 - {DBUpdaterStatus.UpdateCompleted}");
                Console.WriteLine($"            1 - {DBUpdaterStatus.UpdateError}");
                Console.WriteLine($"            2 - {DBUpdaterStatus.UpdateNotNeeded}");
                return 0;
            }
            DevExpress.ExpressApp.FrameworkSettings.DefaultSettingsCompatibilityMode = DevExpress.ExpressApp.FrameworkSettingsCompatibilityMode.Latest;
            DevExpress.ExpressApp.Security.SecurityStrategy.AutoAssociationReferencePropertyMode = DevExpress.ExpressApp.Security.ReferenceWithoutAssociationPermissionsMode.AllMembers;
#if EASYTEST
            DevExpress.ExpressApp.Win.EasyTest.EasyTestRemotingRegistration.Register();
#endif
            WindowsFormsSettings.LoadApplicationSettings();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            DevExpress.Utils.ToolTipController.DefaultController.ToolTipType = DevExpress.Utils.ToolTipType.SuperTip;
            if (Tracing.GetFileLocationFromSettings() == DevExpress.Persistent.Base.FileLocation.CurrentUserApplicationDataFolder)
            {
                Tracing.LocalUserAppDataPath = Application.LocalUserAppDataPath;
            }
            Tracing.Initialize();

            string connectionString = null;
            if (ConfigurationManager.ConnectionStrings["ConnectionString"] != null)
            {
                connectionString = ConfigurationManager.ConnectionStrings["ConnectionString"].ConnectionString;
            }
#if EASYTEST
            if(ConfigurationManager.ConnectionStrings["EasyTestConnectionString"] != null) {
                connectionString = ConfigurationManager.ConnectionStrings["EasyTestConnectionString"].ConnectionString;
            }
#endif
            ArgumentNullException.ThrowIfNull(connectionString);
            var winApplication = ApplicationBuilder.BuildApplication(connectionString);

            if (ContainsArgument(args, "updateDatabase"))
            {
                using var dbUpdater = new WinDBUpdater(() => winApplication);
                return dbUpdater.Update(
                    forceUpdate: ContainsArgument(args, "forceUpdate"),
                    silent: ContainsArgument(args, "silent"));
            }

            try
            {
                winApplication.Setup();

                WireAIServices(winApplication);
                winApplication.Start();
            }
            catch (Exception e)
            {
                winApplication.StopSplash();
                winApplication.HandleException(e);
            }
            return 0;
        }

        /// <summary>
        /// After Setup(): XAF types are registered, so the schema can be discovered, and the one
        /// application scope exists, so the scoped chat client can be handed to the desktop AI container.
        /// </summary>
        private static void WireAIServices(WinApplication winApplication)
        {
            var services = winApplication.ServiceProvider;

            // BuildApplication() ran before Setup(), against an empty ITypesInfo.
            services.GetRequiredService<SchemaDiscoveryService>().InvalidateCache();

            // Tool bodies run on the UI thread: ObjectSpaces come from the application, which is not thread-safe.
            var toolsProvider = services.GetRequiredService<AIToolsProvider>();
            toolsProvider.Application = winApplication;
            var uiContext = SynchronizationContext.Current;
            if (uiContext != null)
            {
                toolsProvider.Dispatch = body =>
                {
                    Task<object> result = null;
                    if (SynchronizationContext.Current == uiContext)
                        return body();
                    uiContext.Send(_ => result = body(), null);   // tool bodies are synchronous: already complete
                    return result;
                };
            }

            // The WinForms application and its DI scope outlive logoff/logon (WinApplication.LogOff
            // re-runs DoLogon in place), so the next user must not inherit this one's conversation,
            // view context or queued navigation (SEC-002, SEC-003).
            winApplication.LoggedOff += (_, _) =>
            {
                services.GetRequiredService<AIChatService>().Reset();
                services.GetRequiredService<ActiveViewContext>().Clear();
                services.GetRequiredService<NavigationRequestQueue>().Clear();
                services.GetRequiredService<AILogScope>().Clear();
            };

            try
            {
                AIExtensionsContainerDesktop.Default.RegisterChatClient(services.GetRequiredService<IChatClient>());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AI chat not available: {ex.Message}");
            }
        }
    }
}
