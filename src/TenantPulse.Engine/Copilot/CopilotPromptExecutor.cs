using System.Text.Json;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Content;
using TenantPulse.Engine.Graph;
using ExecContext = TenantPulse.Core.Activities.ExecutionContext;

namespace TenantPulse.Engine.Copilot;

/// <summary>
/// Sends prompts to Microsoft 365 Copilot as the acting user, via the Graph Copilot Chat API.
/// <para>
/// Two things worth knowing before trusting this for usage reporting:
/// <list type="bullet">
///   <item>The API is <b>preview</b> and lives on <c>/beta</c>. Endpoint shapes have moved before
///   and may move again.</item>
///   <item>Microsoft does <b>not</b> document whether API-driven conversations are counted in the
///   admin centre's Copilot usage reports. Run <c>tenant-pulse verify-copilot</c> to check
///   empirically against the Interaction Export API rather than assuming.</item>
/// </list>
/// It is delegated-only by design, and every actor needs a Microsoft 365 Copilot licence — the
/// planner already filters on <see cref="Core.Personas.Persona.HasCopilotLicence"/>.
/// </para>
/// </summary>
public sealed class CopilotPromptExecutor(
    IGraphClient graph,
    IContentGenerator contentGenerator,
    TenantPulseOptions options,
    ILogger<CopilotPromptExecutor> logger) : IActivityExecutor
{
    public ActivityKind Kind => ActivityKind.CopilotPrompt;

    public async Task<ActivityResult> ExecuteAsync(
        ActivityIntent intent,
        ExecContext context,
        CancellationToken cancellationToken)
    {
        if (!options.Copilot.Enabled)
        {
            return ActivityResult.Skipped("Copilot is disabled in configuration.");
        }

        if (!options.Copilot.UseGraphChatApi)
        {
            return ActivityResult.Skipped("Graph Copilot Chat API is disabled (Copilot.UseGraphChatApi=false).");
        }

        if (!intent.Actor.HasCopilotLicence)
        {
            return ActivityResult.Skipped($"{intent.Actor.UserPrincipalName} has no Copilot licence.");
        }

        var generated = await contentGenerator.GenerateAsync(
            new ContentRequest { Shape = ContentShape.CopilotPrompt, Intent = intent },
            cancellationToken).ConfigureAwait(false);

        var prompt = generated.Body.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return ActivityResult.Skipped("Content generator produced an empty prompt.");
        }

        if (context.DryRun)
        {
            return ActivityResult.Simulated($"Would ask Copilot as {intent.Actor.UserPrincipalName}: \"{Truncate(prompt, 120)}\"");
        }

        var upn = intent.Actor.UserPrincipalName;

        try
        {
            var conversation = await graph
                .PostAsync(upn, "copilot/conversations", new { }, cancellationToken, beta: true)
                .ConfigureAwait(false);

            var conversationId = conversation?.GetStringOrNull("id");
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                return ActivityResult.Failed("Copilot conversation was created but returned no id.");
            }

            var reply = await graph.PostAsync(
                upn,
                $"copilot/conversations/{conversationId}/chat",
                new
                {
                    message = new { text = prompt },
                    locationHint = new { timeZone = intent.Actor.TimeZoneId }
                },
                cancellationToken,
                beta: true).ConfigureAwait(false);

            var answer = ExtractAnswer(reply);

            logger.LogDebug(
                "Copilot answered {Upn} in conversation {ConversationId} ({Length} chars).",
                upn, conversationId, answer?.Length ?? 0);

            return ActivityResult.Executed(
                resourceId: conversationId,
                purgePath: null, // Copilot conversations aren't deletable via the API.
                detail: $"Prompt: \"{Truncate(prompt, 100)}\"" +
                        (answer is null ? "" : $" → answered ({answer.Length} chars)"));
        }
        catch (GraphException ex) when (ex.IsForbidden)
        {
            return ActivityResult.Skipped(
                $"Copilot refused for {upn} (403) — usually a missing Copilot licence or unconsented " +
                $"preview scope. {ex.Message}");
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            return ActivityResult.Skipped(
                "Copilot Chat API returned 404 — the preview endpoint has likely moved. " +
                "Set Copilot.UseGraphChatApi=false until the path is updated.");
        }
        catch (GraphException ex)
        {
            return ActivityResult.Failed($"Copilot call failed ({(int)ex.StatusCode}): {ex.Message}");
        }
    }

    /// <summary>
    /// The preview response shape has changed between iterations, so pull the text out of whichever
    /// of the known shapes came back rather than binding to one.
    /// </summary>
    private static string? ExtractAnswer(JsonElement? response)
    {
        if (response is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var direct = element.GetStringOrNull("text")
                     ?? element.GetNestedString("message", "text")
                     ?? element.GetNestedString("response", "text");

        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        foreach (var arrayName in (string[])["messages", "value", "responses"])
        {
            if (!element.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in array.EnumerateArray().Reverse())
            {
                var text = item.GetStringOrNull("text") ?? item.GetNestedString("body", "content");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}
