using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TenantPulse.Core;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Content;
using ExecContext = TenantPulse.Core.Activities.ExecutionContext;

namespace TenantPulse.Engine.Copilot;

/// <summary>
/// Talks to Copilot Studio agents over the Bot Framework Direct Line API.
/// <para>
/// Unlike the Copilot Chat API this is a stable, documented, GA surface, and conversations show up
/// in Copilot Studio's own analytics — so it's the most reliable way to make agents look used.
/// The conversation is bound to the acting persona's UPN so sessions aren't all attributed to one
/// anonymous user.
/// </para>
/// </summary>
public sealed class AgentPromptExecutor(
    IHttpClientFactory httpClientFactory,
    IContentGenerator contentGenerator,
    TenantPulseOptions options,
    ILogger<AgentPromptExecutor> logger) : IActivityExecutor
{
    public ActivityKind Kind => ActivityKind.AgentPrompt;

    public async Task<ActivityResult> ExecuteAsync(
        ActivityIntent intent,
        ExecContext context,
        CancellationToken cancellationToken)
    {
        var agents = options.Copilot.Agents
            .Where(a => !string.IsNullOrWhiteSpace(a.DirectLineSecret))
            .ToList();

        if (agents.Count == 0)
        {
            return ActivityResult.Skipped("No Copilot Studio agents configured with a Direct Line secret.");
        }

        var rng = Core.DeterministicRandom.For(context.Seed, "agent", intent.Id);
        var agent = agents[rng.Next(agents.Count)];

        var prompt = await ResolvePromptAsync(intent, agent, rng, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return ActivityResult.Skipped("No prompt available for the agent.");
        }

        if (context.DryRun)
        {
            return ActivityResult.Simulated(
                $"Would ask agent '{agent.Name}' as {intent.Actor.UserPrincipalName}: \"{prompt}\"");
        }

        try
        {
            using var client = httpClientFactory.CreateClient(nameof(AgentPromptExecutor));
            client.BaseAddress = new Uri(agent.Endpoint.TrimEnd('/') + "/");

            var conversationId = await StartConversationAsync(client, agent, intent, cancellationToken)
                .ConfigureAwait(false);

            if (conversationId is null)
            {
                return ActivityResult.Failed($"Direct Line did not return a conversation id for '{agent.Name}'.");
            }

            using var activityRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"v3/directline/conversations/{conversationId}/activities")
            {
                Content = JsonContent.Create(new
                {
                    type = "message",
                    from = new { id = intent.Actor.UserPrincipalName, name = intent.Actor.DisplayName },
                    text = prompt
                })
            };

            using var response = await client.SendAsync(activityRequest, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ActivityResult.Failed(
                    $"Direct Line rejected the message for '{agent.Name}' ({(int)response.StatusCode}): {body}");
            }

            logger.LogDebug("Asked agent {Agent} as {Upn}.", agent.Name, intent.Actor.UserPrincipalName);

            return ActivityResult.Executed(
                resourceId: conversationId,
                purgePath: null, // Direct Line conversations expire on their own.
                detail: $"Agent '{agent.Name}': \"{prompt}\"");
        }
        catch (HttpRequestException ex)
        {
            return ActivityResult.Failed($"Direct Line call to '{agent.Name}' failed: {ex.Message}");
        }
    }

    private async Task<string?> StartConversationAsync(
        HttpClient client,
        AgentOptions agent,
        ActivityIntent intent,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v3/directline/conversations");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", agent.DirectLineSecret);

        // Binds the session to the persona so agent analytics don't show one anonymous super-user.
        request.Content = JsonContent.Create(new
        {
            user = new { id = $"dl_{intent.Actor.Id}", name = intent.Actor.DisplayName }
        });

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty("conversationId", out var id)
            ? id.GetString()
            : null;
    }

    private async Task<string?> ResolvePromptAsync(
        ActivityIntent intent,
        AgentOptions agent,
        Random rng,
        CancellationToken cancellationToken)
    {
        // A configured sample prompt is more likely to be something the agent can actually answer
        // than anything generated blind, so prefer those most of the time.
        if (agent.SamplePrompts.Count > 0 && rng.Chance(0.7))
        {
            return agent.SamplePrompts[rng.Next(agent.SamplePrompts.Count)];
        }

        var generated = await contentGenerator.GenerateAsync(
            new ContentRequest
            {
                Shape = ContentShape.AgentPrompt,
                Intent = intent,
                Context = [$"The agent is called '{agent.Name}'."]
            },
            cancellationToken).ConfigureAwait(false);

        return generated.Body.Trim();
    }
}
