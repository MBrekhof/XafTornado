using System;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace XafTornado.Module.Services
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddAIServices(this IServiceCollection services, IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            services.Configure<AIOptions>(configuration.GetSection(AIOptions.SectionName));
            services.AddSingleton<SchemaDiscoveryService>();
            // Tools + system prompt are wired here so every consumer (DxAIChat via IChatClient,
            // TestApiController, WinForms) gets the same configured singleton.
            services.AddSingleton<AIChatService>(sp =>
            {
                var service = new AIChatService(
                    sp.GetRequiredService<IOptions<AIOptions>>(),
                    sp.GetRequiredService<ILogger<AIChatService>>());
                var toolsProvider = sp.GetRequiredService<AIToolsProvider>();
                service.ToolFunctions = toolsProvider.Tools;
                service.TornadoTools = toolsProvider.GetTornadoTools();
                service.SystemMessage = sp.GetRequiredService<SchemaDiscoveryService>().GenerateSystemPrompt();
                return service;
            });
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
            services.AddChatClient(sp => new AIChatClient(sp.GetRequiredService<AIChatService>()));

            return services;
        }
    }
}
