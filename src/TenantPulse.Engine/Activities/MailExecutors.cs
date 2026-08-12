using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Content;
using TenantPulse.Core.Personas;
using TenantPulse.Engine.Graph;
using ExecContext = TenantPulse.Core.Activities.ExecutionContext;

namespace TenantPulse.Engine.Activities;

public sealed class SendMailExecutor(
    IGraphClient graph,
    IContentGenerator contentGenerator,
    TenantPulseOptions options,
    ILogger<SendMailExecutor> logger) : IActivityExecutor
{
    public ActivityKind Kind => ActivityKind.SendMail;

    public async Task<ActivityResult> ExecuteAsync(
        ActivityIntent intent,
        ExecContext context,
        CancellationToken cancellationToken)
    {
        if (intent.Targets.Count is 0)
        {
            return ActivityResult.Skipped("No mail recipients were provided.");
        }

        try
        {
            var content = await contentGenerator.GenerateAsync(
                new ContentRequest { Shape = ContentShape.EmailNew, Intent = intent },
                cancellationToken).ConfigureAwait(false);

            var subject = ExecutorHelpers.SubjectOrFallback(content.Subject, intent.Topic);
            if (context.DryRun)
            {
                return ActivityResult.Simulated(
                    $"Would send '{subject}' from {intent.Actor.UserPrincipalName} to {intent.Targets.Count} recipient(s).");
            }

            var upn = intent.Actor.UserPrincipalName;
            var draft = await graph.PostAsync(
                upn,
                $"users/{upn}/messages",
                new
                {
                    subject,
                    body = new { contentType = "HTML", content = ExecutorHelpers.ToHtmlParagraphs(content.Body) },
                    toRecipients = intent.Targets.Select(ExecutorHelpers.ToRecipient).ToArray(),
                    internetMessageHeaders = new[]
                    {
                        new { name = MarkerHeaderName(options), value = options.Simulation.MarkerValue }
                    }
                },
                cancellationToken).ConfigureAwait(false);

            var messageId = draft?.GetStringOrNull("id");
            if (string.IsNullOrWhiteSpace(messageId))
            {
                return ActivityResult.Failed("Graph did not return a draft message id.");
            }

            // internetMessageId survives the send; the item id does not (see below).
            var internetMessageId = draft?.GetStringOrNull("internetMessageId");

            // Graph does not accept custom internetMessageHeaders on sendMail; create-then-send preserves the marker.
            await graph.PostAsync(upn, $"users/{upn}/messages/{messageId}/send", new { }, cancellationToken)
                .ConfigureAwait(false);

            // Sending moves the message from Drafts to Sent Items and Exchange assigns it a NEW id,
            // so the draft id is useless for purging. Resolve the sent copy by its internet message
            // id, which is stable across the move.
            var sentId = await ResolveSentMessageIdAsync(upn, internetMessageId, cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation("Sent simulated mail {MessageId} as {UserPrincipalName}.",
                sentId ?? messageId, upn);

            return ActivityResult.Executed(
                sentId ?? messageId,
                sentId is null ? null : $"users/{upn}/messages/{sentId}",
                $"Sent '{subject}' to {intent.Targets.Count} recipient(s)." +
                (sentId is null ? " (sent copy not resolved, so it cannot be purged)" : string.Empty));
        }
        catch (UserNotEnrolledException ex)
        {
            return ActivityResult.Skipped(ex.Message);
        }
        catch (GraphException ex) when (ex.IsForbidden || ex.IsNotFound)
        {
            return ActivityResult.Skipped(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send mail for {IntentId}.", intent.Id);
            return ActivityResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Finds the Sent Items copy of a message that has just been sent, by its internet message id.
    /// Returns null when it can't be resolved — in which case the mail is still sent, it just can't
    /// be purged later.
    /// </summary>
    private async Task<string?> ResolveSentMessageIdAsync(
        string upn,
        string? internetMessageId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(internetMessageId))
        {
            return null;
        }

        try
        {
            // Exchange indexes the sent copy asynchronously, so a first miss is normal.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var escaped = internetMessageId.Replace("'", "''", StringComparison.Ordinal);
                var results = await graph.GetPagedAsync(
                    upn,
                    $"users/{upn}/mailFolders/sentitems/messages" +
                    $"?$filter=internetMessageId eq '{Uri.EscapeDataString(escaped)}'&$select=id&$top=1",
                    maxItems: 1,
                    cancellationToken).ConfigureAwait(false);

                var id = results.Count > 0 ? results[0].GetStringOrNull("id") : null;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return id;
                }

                await Task.Delay(TimeSpan.FromSeconds(1 + attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (GraphException ex)
        {
            logger.LogDebug("Could not resolve the sent copy for {Upn} ({Status}); it will not be purgeable.",
                upn, ex.StatusCode);
        }

        return null;
    }

    private static string MarkerHeaderName(TenantPulseOptions options)
    {
        var configured = string.IsNullOrWhiteSpace(options.Simulation.MarkerHeaderName)
            ? "X-TenantPulse"
            : options.Simulation.MarkerHeaderName.Trim();
        return configured.StartsWith("X-", StringComparison.OrdinalIgnoreCase) ? configured : $"X-{configured}";
    }
}

public sealed class ReplyMailExecutor(
    IGraphClient graph,
    IContentGenerator contentGenerator,
    ILogger<ReplyMailExecutor> logger) : IActivityExecutor
{
    public ActivityKind Kind => ActivityKind.ReplyMail;

    public async Task<ActivityResult> ExecuteAsync(
        ActivityIntent intent,
        ExecContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var upn = intent.Actor.UserPrincipalName;
            var inbox = await graph.GetAsync(
                upn,
                $"users/{upn}/mailFolders/inbox/messages?$top=25&$select=id,subject,from,receivedDateTime,conversationId,bodyPreview&$orderby=receivedDateTime desc",
                cancellationToken).ConfigureAwait(false);

            var message = inbox.GetValueArray()
                .FirstOrDefault(m => !string.Equals(m.GetNestedString("from", "emailAddress", "address"), upn, StringComparison.OrdinalIgnoreCase));

            if (message.ValueKind is JsonValueKind.Undefined)
            {
                return ActivityResult.Skipped("Inbox has no suitable recent message to reply to.");
            }

            var messageId = message.GetStringOrNull("id");
            if (string.IsNullOrWhiteSpace(messageId))
            {
                return ActivityResult.Skipped("Selected inbox message has no id.");
            }

            var subject = message.GetStringOrNull("subject") ?? intent.Topic;
            var bodyPreview = message.GetStringOrNull("bodyPreview");
            var content = await contentGenerator.GenerateAsync(
                new ContentRequest
                {
                    Shape = ContentShape.EmailReply,
                    Intent = intent,
                    InReplyTo = bodyPreview,
                    ThreadSubject = subject
                },
                cancellationToken).ConfigureAwait(false);

            if (context.DryRun)
            {
                return ActivityResult.Simulated($"Would reply to '{subject}' in {upn}'s inbox.");
            }

            await graph.PostAsync(
                upn,
                $"users/{upn}/messages/{messageId}/reply",
                new { comment = content.Body },
                cancellationToken).ConfigureAwait(false);

            return ActivityResult.Executed(detail: $"Replied to '{subject}'.");
        }
        catch (UserNotEnrolledException ex)
        {
            return ActivityResult.Skipped(ex.Message);
        }
        catch (GraphException ex) when (ex.IsForbidden || ex.IsNotFound)
        {
            return ActivityResult.Skipped(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reply to mail for {IntentId}.", intent.Id);
            return ActivityResult.Failed(ex.Message);
        }
    }
}

public sealed class ReadMailExecutor(
    IGraphClient graph,
    ILogger<ReadMailExecutor> logger) : IActivityExecutor
{
    public ActivityKind Kind => ActivityKind.ReadMail;

    public async Task<ActivityResult> ExecuteAsync(
        ActivityIntent intent,
        ExecContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var upn = intent.Actor.UserPrincipalName;
            var messages = await graph.GetAsync(
                upn,
                $"users/{upn}/mailFolders/inbox/messages?$filter=isRead eq false&$top=5&$select=id,subject,receivedDateTime&$orderby=receivedDateTime desc",
                cancellationToken).ConfigureAwait(false);

            var unread = messages.GetValueArray()
                .Where(m => !string.IsNullOrWhiteSpace(m.GetStringOrNull("id")))
                .ToArray();

            if (unread.Length is 0)
            {
                return ActivityResult.Skipped("Inbox has no unread messages.");
            }

            var rng = ExecutorHelpers.RandomFor(context.Seed, intent.Id);
            var selected = unread.Take(Math.Min(unread.Length, rng.Next(1, 6))).ToArray();
            var flagIndex = rng.NextDouble() < 0.25d ? rng.Next(selected.Length) : -1;

            if (context.DryRun)
            {
                return ActivityResult.Simulated($"Would mark {selected.Length} unread inbox message(s) as read.");
            }

            for (var i = 0; i < selected.Length; i++)
            {
                var id = selected[i].GetStringOrNull("id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                object body = i == flagIndex
                    ? new { isRead = true, flag = new { flagStatus = "flagged" } }
                    : new { isRead = true };
                await graph.PatchAsync(upn, $"users/{upn}/messages/{id}", body, cancellationToken).ConfigureAwait(false);
            }

            return ActivityResult.Executed(detail: $"Marked {selected.Length} inbox message(s) as read.");
        }
        catch (UserNotEnrolledException ex)
        {
            return ActivityResult.Skipped(ex.Message);
        }
        catch (GraphException ex) when (ex.IsForbidden || ex.IsNotFound)
        {
            return ActivityResult.Skipped(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read mail for {IntentId}.", intent.Id);
            return ActivityResult.Failed(ex.Message);
        }
    }
}

internal static partial class ExecutorHelpers
{
    public static object ToRecipient(Persona persona) => new
    {
        emailAddress = new { address = persona.UserPrincipalName, name = persona.DisplayName }
    };

    public static string SubjectOrFallback(string? subject, string topic) =>
        string.IsNullOrWhiteSpace(subject) ? topic : subject.Trim();

    public static string ToHtmlParagraphs(string text)
    {
        var paragraphs = ParagraphSplitter().Split(text.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => $"<p>{HtmlEncoder.Default.Encode(p.Trim())}</p>");
        return string.Join("", paragraphs);
    }

    public static Random RandomFor(int seed, string intentId)
    {
        var hash = seed;
        foreach (var c in intentId)
        {
            hash = unchecked((hash * 31) + c);
        }

        return new Random(hash);
    }

    public static string SafeFileSegment(string value)
    {
        var sanitized = InvalidSegmentChars().Replace(value, "-").Trim('-', ' ', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "Working Notes" : sanitized;
    }

    [GeneratedRegex("\\r?\\n\\s*\\r?\\n|\\r?\\n")]
    private static partial Regex ParagraphSplitter();

    [GeneratedRegex("[^A-Za-z0-9 ._-]+")]
    private static partial Regex InvalidSegmentChars();
}

internal static class ActivityJsonHelpers
{
    public static IReadOnlyList<JsonElement> GetArrayOrEmpty(this JsonElement element, string propertyName)
    {
        if (element.ValueKind is not JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        return [.. property.EnumerateArray()];
    }

    public static bool StringPropertyEquals(this JsonElement element, string propertyName, string expected) =>
        string.Equals(element.GetStringOrNull(propertyName), expected, StringComparison.OrdinalIgnoreCase);
}
