using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Content;
using TenantPulse.Core.Journaling;
using TenantPulse.Core.Personas;
using TenantPulse.Core.Safety;
using TenantPulse.Core.Time;
using TenantPulse.Engine.Activities;
using TenantPulse.Engine.Auth;
using TenantPulse.Engine.Content;
using TenantPulse.Engine.Copilot;
using TenantPulse.Engine.Graph;
using TenantPulse.Engine.Journaling;
using TenantPulse.Engine.Personas;

namespace TenantPulse.Engine;

/// <summary>Wires the whole simulator together.</summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddTenantPulse(this IServiceCollection services, TenantPulseOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<SafetyGovernor>();

        // A table is readable from anywhere, which is what makes a hosted run observable and lets
        // report and purge run from a laptop. SQLite stays the default for local use.
        if (options.Simulation.JournalTable.IsConfigured)
        {
            services.AddSingleton<IActivityJournal, AzureTableActivityJournal>();
        }
        else
        {
            services.AddSingleton<IActivityJournal, SqliteActivityJournal>();
        }

        services.AddSingleton<UserTokenBroker>();
        services.AddSingleton<IUserTokenProvider>(sp => sp.GetRequiredService<UserTokenBroker>());

        services.AddHttpClient();
        services.AddHttpClient<IGraphClient, GraphClient>();

        services.AddSingleton<GraphPersonaDirectory>();
        services.AddSingleton<CopilotUsageVerifier>();
        services.AddSingleton<PulseEngine>();

        AddContentGeneration(services, options);
        AddExecutors(services);

        return services;
    }

    private static void AddContentGeneration(IServiceCollection services, TenantPulseOptions options)
    {
        services.AddSingleton<ContentPromptBuilder>();
        services.AddSingleton<TemplateContentGenerator>();

        if (options.Content.Provider != ContentProvider.AzureOpenAI)
        {
            services.AddSingleton<IContentGenerator>(sp => sp.GetRequiredService<TemplateContentGenerator>());
            return;
        }

        services.AddSingleton<IContentGenerator>(sp =>
        {
            var templates = sp.GetRequiredService<TemplateContentGenerator>();

            // A misconfigured or absent Azure OpenAI must never stop the simulator: read-only
            // commands (doctor, plan) still need a content generator, and a live run should keep
            // going on templates rather than falling over.
            try
            {
                var azure = new AzureOpenAIContentGenerator(
                    options,
                    sp.GetRequiredService<ContentPromptBuilder>(),
                    sp.GetRequiredService<ILogger<AzureOpenAIContentGenerator>>());

                return new ResilientContentGenerator(
                    azure, templates, options,
                    sp.GetRequiredService<ILogger<ResilientContentGenerator>>());
            }
            catch (Exception ex)
            {
                sp.GetRequiredService<ILogger<TemplateContentGenerator>>().LogWarning(
                    "Azure OpenAI is not usable ({Reason}); falling back to template content. " +
                    "Run 'tenant-pulse doctor' for details.", ex.GetBaseException().Message);

                return templates;
            }
        });
    }

    private static void AddExecutors(IServiceCollection services)
    {
        services.AddSingleton<IActivityExecutor, SendMailExecutor>();
        services.AddSingleton<IActivityExecutor, ReplyMailExecutor>();
        services.AddSingleton<IActivityExecutor, ReadMailExecutor>();

        services.AddSingleton<IActivityExecutor, ChatMessageExecutor>();
        services.AddSingleton<IActivityExecutor, ChannelPostExecutor>();
        services.AddSingleton<IActivityExecutor, ChannelReplyExecutor>();
        services.AddSingleton<IActivityExecutor, ReactionExecutor>();

        services.AddSingleton<IActivityExecutor, CreateDocumentExecutor>();
        services.AddSingleton<IActivityExecutor, EditDocumentExecutor>();

        services.AddSingleton<IActivityExecutor, CreateEventExecutor>();

        services.AddSingleton<IActivityExecutor, CopilotPromptExecutor>();
        services.AddSingleton<IActivityExecutor, AgentPromptExecutor>();
    }
}
