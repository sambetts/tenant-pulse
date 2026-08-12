using System.Text.Json;
using Microsoft.Extensions.Logging;
using TenantPulse.Engine.Graph;

namespace TenantPulse.Engine.Copilot;

public sealed record CopilotVerificationResult(
    bool PromptSent,
    string? ConversationId,
    int InteractionsFound,
    bool FoundOurPrompt,
    string Explanation);

/// <summary>
/// Answers, empirically, the one thing Microsoft's documentation does not: whether a prompt sent
/// through the Graph Copilot Chat API is recorded as real Copilot usage for that user.
/// <para>
/// It sends a uniquely-marked prompt as a user, waits, then reads that user's interaction history
/// back through the Copilot Interaction Export API (<c>getAllEnterpriseInteractions</c>) looking
/// for the marker. If the marker comes back, API-driven prompts are landing in the same store the
/// compliance/usage surfaces read from. If not, switch to the browser driver.
/// </para>
/// <para>
/// The export API is <b>application-permission only</b> (<c>AiEnterpriseInteraction.Read.All</c>),
/// so verification needs an app-only token — unlike everything else in tenant-pulse.
/// </para>
/// </summary>
public sealed class CopilotUsageVerifier(
    IGraphClient graph,
    ILogger<CopilotUsageVerifier> logger)
{
    public async Task<CopilotVerificationResult> VerifyAsync(
        string userPrincipalName,
        Func<CancellationToken, Task<string?>> appOnlyTokenFactory,
        TimeSpan settleDelay,
        CancellationToken cancellationToken)
    {
        var marker = $"tenant-pulse verification {Guid.NewGuid():N}";
        var prompt = $"In one short sentence, what is a good agenda for a weekly team meeting? ({marker})";

        string? conversationId = null;

        try
        {
            var conversation = await graph
                .PostAsync(userPrincipalName, "copilot/conversations", new { }, cancellationToken, beta: true)
                .ConfigureAwait(false);

            conversationId = conversation?.GetStringOrNull("id");

            if (conversationId is null)
            {
                return new CopilotVerificationResult(false, null, 0, false,
                    "Could not create a Copilot conversation — the preview endpoint may have moved or " +
                    "the user may lack a Copilot licence.");
            }

            await graph.PostAsync(
                userPrincipalName,
                $"copilotConversations/{conversationId}/chat",
                new { message = new { text = prompt } },
                cancellationToken,
                beta: true).ConfigureAwait(false);
        }
        catch (GraphException ex)
        {
            return new CopilotVerificationResult(false, conversationId, 0, false,
                $"Sending the test prompt failed ({(int)ex.StatusCode}): {ex.Message}");
        }

        var token = await appOnlyTokenFactory(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new CopilotVerificationResult(true, conversationId, 0, false,
                "Prompt sent, but no app-only token was available to read the interaction history. " +
                "Grant the app the AiEnterpriseInteraction.Read.All application permission to complete " +
                "verification, or check the admin centre Copilot usage report manually tomorrow.");
        }

        // Interactions are not written synchronously; give the pipeline a moment.
        logger.LogInformation("Prompt sent. Waiting {Delay} for the interaction to be recorded…", settleDelay);
        await Task.Delay(settleDelay, cancellationToken).ConfigureAwait(false);

        try
        {
            var path = $"copilot/users/{Uri.EscapeDataString(userPrincipalName)}" +
                       "/interactionHistory/getAllEnterpriseInteractions?$top=50";

            var response = await graph
                .GetWithTokenAsync(path, token, cancellationToken, beta: true)
                .ConfigureAwait(false);

            var interactions = response.GetValueArray();
            var found = interactions.Any(i =>
                (i.GetStringOrNull("body") ?? i.GetNestedString("body", "content") ?? string.Empty)
                    .Contains(marker, StringComparison.OrdinalIgnoreCase));

            var explanation = found
                ? "CONFIRMED: the prompt sent via the Graph Copilot Chat API appears in the user's " +
                  "Copilot interaction history, so API-driven prompts are recorded as genuine usage."
                : $"NOT FOUND: {interactions.Count} interaction(s) were returned but none contained the " +
                  "marker. Either the export pipeline is still catching up (retry with a longer delay), " +
                  "or API-driven prompts are not recorded — in which case use the browser driver for " +
                  "Copilot activity.";

            return new CopilotVerificationResult(true, conversationId, interactions.Count, found, explanation);
        }
        catch (GraphException ex)
        {
            return new CopilotVerificationResult(true, conversationId, 0, false,
                $"Prompt sent, but reading the interaction history failed ({(int)ex.StatusCode}): {ex.Message}. " +
                "The app registration likely needs the AiEnterpriseInteraction.Read.All application permission " +
                "with admin consent.");
        }
    }

    /// <summary>Reads how many interactions a user has, as a cheap "is Copilot being used" probe.</summary>
    public async Task<int> CountInteractionsAsync(
        string userPrincipalName,
        string appOnlyToken,
        CancellationToken cancellationToken)
    {
        var path = $"copilot/users/{Uri.EscapeDataString(userPrincipalName)}" +
                   "/interactionHistory/getAllEnterpriseInteractions?$top=100";

        var response = await graph.GetWithTokenAsync(path, appOnlyToken, cancellationToken, beta: true)
            .ConfigureAwait(false);

        return response.GetValueArray().Count;
    }

    internal static string DescribeJson(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? "(none)" : element.ToString();
}
