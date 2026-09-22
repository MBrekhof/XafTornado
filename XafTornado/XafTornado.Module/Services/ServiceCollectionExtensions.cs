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

            // Process-wide: the schema and the TornadoApi carry no per-user state.
            services.AddSingleton<SchemaDiscoveryService>();
            services.AddSingleton<TornadoApiProvider>();

            // Log store + logger provider for the AI log viewer panel.
            services.AddSingleton<AILogStore>();
            services.AddSingleton<ILoggerProvider, AILoggerProvider>();

            // Per user: one instance per Blazor circuit, one per WinForms process (SEC-002, SEC-003).
            // The tools are bound instance delegates on the scope's provider, so the conversation
            // that executes them must come from the same scope.
            services.AddScoped<ActiveViewContext>();
            services.AddScoped<AIToolsProvider>(sp =>
                new AIToolsProvider(
                    sp,
                    sp.GetRequiredService<SchemaDiscoveryService>(),
                    sp.GetService<INavigationService>(),
                    sp.GetRequiredService<ActiveViewContext>()));
            services.AddScoped<AIChatService>(sp =>
            {
                var service = new AIChatService(
                    sp.GetRequiredService<TornadoApiProvider>(),
                    sp.GetRequiredService<IOptions<AIOptions>>(),
                    sp.GetRequiredService<ILogger<AIChatService>>());
                var toolsProvider = sp.GetRequiredService<AIToolsProvider>();
                service.ToolFunctions = toolsProvider.Tools;
                service.TornadoTools = toolsProvider.GetTornadoTools();
                service.SystemPromptFactory = sp.GetRequiredService<SchemaDiscoveryService>().GenerateSystemPrompt;
                return service;
            });

            // The IChatClient adapter DxAIChat / AIChatControl resolve: scoped, so DevExpress's
            // per-circuit IChatResponseProvider (AddDevExpressAI) wraps this circuit's conversation.
            services.AddChatClient(sp => new AIChatClient(sp.GetRequiredService<AIChatService>()), ServiceLifetime.Scoped);

            return services;
        }
    }
}
