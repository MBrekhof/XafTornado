using System;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace XafTornado.Module.Services
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddAIServices(this IServiceCollection services, IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            services.Configure<AIOptions>(configuration.GetSection(AIOptions.SectionName));
            services.AddSingleton<AIChatService>();
            services.AddSingleton<SchemaDiscoveryService>();
            services.AddSingleton<ActiveViewContext>();

            // Log store + logger provider for the AI log viewer panel.
            services.AddSingleton<AILogStore>();
            services.AddSingleton<ILoggerProvider, AILoggerProvider>();

            // Register the tools provider (singleton — tools are created lazily on first access).
            services.AddSingleton<AIToolsProvider>(sp =>
                new AIToolsProvider(
                    sp,
                    sp.GetRequiredService<SchemaDiscoveryService>(),
                    sp.GetService<INavigationService>(),
                    sp.GetService<ActiveViewContext>()));

            // Register the IChatClient adapter so DevExpress DxAIChat / AIChatControl
            // can route messages through LLMTornado automatically.
            services.AddChatClient(sp =>
            {
                var service = sp.GetRequiredService<AIChatService>();
                var toolsProvider = sp.GetRequiredService<AIToolsProvider>();
                var schemaService = sp.GetRequiredService<SchemaDiscoveryService>();

                // Wire tools — both AIFunction (for execution) and LLMTornado Tool (for schema)
                service.ToolFunctions = toolsProvider.Tools;
                service.TornadoTools = toolsProvider.GetTornadoTools();
                service.SystemMessage = schemaService.GenerateSystemPrompt();

                return new AIChatClient(service);
            });

            return services;
        }
    }
}
