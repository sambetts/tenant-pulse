using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Content;
using TenantPulse.Core.Personas;
using TenantPulse.Engine.Graph;
using ExecContext = TenantPulse.Core.Activities.ExecutionContext;

namespace TenantPulse.Engine.Activities;

public sealed class ChatMessageExecutor(
    IGraphClient graph,
    IContentGenerator contentGenerator,
    TenantPulseOptions options,
    ILogger<ChatMessageExecutor> logger) : IActivityExecutor
{
    public ActivityKind Kind => ActivityKind.ChatMessage;

    public async Task<ActivityResult> ExecuteAsync(
        ActivityIntent intent,
        ExecContext context,
        CancellationToken cancellationToken)
    {
        if (intent.Targets.Count is 0)
        {
            return ActivityResult.Skipped("No chat recipient was provided.");
        }

        try
        {
            var actor = intent.Actor;
            var target = intent.Targets[0];
            var chatId = await FindOneOnOneChatAsync(actor, target, cancellationToken).ConfigureAwait(false);
            var content = await contentGenerator.GenerateAsync(
                new ContentRequest { Shape = ContentShape.TeamsChat, Intent = intent },
                cancellationToken).ConfigureAwait(false);

            if (context.DryRun)
            {
                var action = chatId is null ? "create a 1:1 chat and post" : "post";
                return ActivityResult.Simulated($"Would {action} a Teams chat message to {target.DisplayName}.");
            }

            chatId ??= await CreateOneOnOneChatAsync(actor, target, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(chatId))
            {
                return ActivityResult.Skipped("Could not resolve or create a 1:1 chat.");
            }

            var message = await graph.PostAsync(
                actor.UserPrincipalName,
                $"chats/{chatId}/messages",
                new { body = new { contentType = "html", content = ExecutorHelpers.ToHtmlParagraphs(content.Body) } },
                cancellationToken).ConfigureAwait(false);
            var messageId = message?.GetStringOrNull("id");

            return ActivityResult.Executed(
                messageId,
                messageId is null ? null : $"chats/{chatId}/messages/{messageId}/softDelete",
                "Posted Teams chat message. Purge path requires POST softDelete, not DELETE.");
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
            logger.LogError(ex, "Failed to post chat message for {IntentId}.", intent.Id);
            return ActivityResult.Failed(ex.Message);
        }
    }

    private async Task<string?> FindOneOnOneChatAsync(Persona actor, Persona target, CancellationToken ct)
    {
        var chats = await graph.GetAsync(
            actor.UserPrincipalName,
            $"users/{actor.UserPrincipalName}/chats?$filter=chatType eq 'oneOnOne'&$expand=members&$top=50",
            ct).ConfigureAwait(false);

        foreach (var chat in chats.GetValueArray())
        {
            var members = chat.GetArrayOrEmpty("members");
            if (members.Any(m => string.Equals(m.GetStringOrNull("userId"), target.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return chat.GetStringOrNull("id");
            }
        }

        return null;
    }

    private async Task<string?> CreateOneOnOneChatAsync(Persona actor, Persona target, CancellationToken ct)
    {
        var graphRoot = options.Tenant.GraphBaseUrl.TrimEnd('/');
        var members = new[]
        {
            TeamsPayloads.Member(actor.Id, graphRoot),
            TeamsPayloads.Member(target.Id, graphRoot)
        };
        var chat = await graph.PostAsync(
            actor.UserPrincipalName,
            "chats",
            new { chatType = "oneOnOne", members },
            ct).ConfigureAwait(false);
        return chat?.GetStringOrNull("id");
    }
}

public sealed class ChannelPostExecutor(
    IGraphClient graph,
    IContentGenerator contentGenerator,
    TenantPulseOptions options,
    ILogger<ChannelPostExecutor> logger) : IActivityExecutor
{
    public ActivityKind Kind => ActivityKind.ChannelPost;

    public async Task<ActivityResult> ExecuteAsync(
        ActivityIntent intent,
        ExecContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var teamChannel = await TeamsHelpers.PickTeamChannelAsync(graph, options, intent, context, cancellationToken).ConfigureAwait(false);
            if (teamChannel is null)
            {
                return ActivityResult.Skipped("Actor has no eligible standard Teams channel.");
            }

            var content = await contentGenerator.GenerateAsync(
                new ContentRequest { Shape = ContentShape.TeamsChannelPost, Intent = intent },
                cancellationToken).ConfigureAwait(false);
            var subject = ExecutorHelpers.SubjectOrFallback(content.Subject, intent.Topic);

            if (context.DryRun)
            {
                return ActivityResult.Simulated($"Would post '{subject}' to {teamChannel.Value.TeamName}/{teamChannel.Value.ChannelName}.");
            }

            var message = await graph.PostAsync(
                intent.Actor.UserPrincipalName,
                $"teams/{teamChannel.Value.TeamId}/channels/{teamChannel.Value.ChannelId}/messages",
                new
                {
                    subject,
                    body = new { contentType = "html", content = ExecutorHelpers.ToHtmlParagraphs(content.Body) }
                },
                cancellationToken).ConfigureAwait(false);
            var messageId = message?.GetStringOrNull("id");

            return ActivityResult.Executed(
                messageId,
                messageId is null ? null : $"teams/{teamChannel.Value.TeamId}/channels/{teamChannel.Value.ChannelId}/messages/{messageId}/softDelete",
                "Posted channel message. Purge path requires POST softDelete, not DELETE.");
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
            logger.LogError(ex, "Failed to post channel message for {IntentId}.", intent.Id);
            return ActivityResult.Failed(ex.Message);
        }
    }
}

public sealed class ChannelReplyExecutor(
    IGraphClient graph,
    IContentGenerator contentGenerator,
    TenantPulseOptions options,
    ILogger<ChannelReplyExecutor> logger) : IActivityExecutor
{
    public ActivityKind Kind => ActivityKind.ChannelReply;

    public async Task<ActivityResult> ExecuteAsync(
        ActivityIntent intent,
        ExecContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var target = await TeamsHelpers.FindChannelMessageAsync(graph, options, intent, context, cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                return ActivityResult.Skipped("No suitable recent channel message was found.");
            }

            var content = await contentGenerator.GenerateAsync(
                new ContentRequest
                {
                    Shape = ContentShape.TeamsReply,
                    Intent = intent,
                    InReplyTo = target.Value.BodyPreview,
                    ThreadSubject = target.Value.Subject
                },
                cancellationToken).ConfigureAwait(false);

            if (context.DryRun)
            {
                return ActivityResult.Simulated($"Would reply in {target.Value.TeamName}/{target.Value.ChannelName}.");
            }

            var reply = await graph.PostAsync(
                intent.Actor.UserPrincipalName,
                $"teams/{target.Value.TeamId}/channels/{target.Value.ChannelId}/messages/{target.Value.MessageId}/replies",
                new { body = new { contentType = "html", content = ExecutorHelpers.ToHtmlParagraphs(content.Body) } },
                cancellationToken).ConfigureAwait(false);
            var replyId = reply?.GetStringOrNull("id");

            return ActivityResult.Executed(
                replyId,
                replyId is null ? null : $"teams/{target.Value.TeamId}/channels/{target.Value.ChannelId}/messages/{target.Value.MessageId}/replies/{replyId}/softDelete",
                "Replied to channel thread. Purge path requires POST softDelete, not DELETE.");
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
            logger.LogError(ex, "Failed to reply to channel message for {IntentId}.", intent.Id);
            return ActivityResult.Failed(ex.Message);
        }
    }
}

public sealed class ReactionExecutor(
    IGraphClient graph,
    TenantPulseOptions options,
    ILogger<ReactionExecutor> logger) : IActivityExecutor
{
    public ActivityKind Kind => ActivityKind.Reaction;

    public async Task<ActivityResult> ExecuteAsync(
        ActivityIntent intent,
        ExecContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var target = await FindReactionTargetAsync(intent, context, cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                return ActivityResult.Skipped("No suitable Teams message was found to react to.");
            }

            var reaction = PickReaction(context, intent.Id);
            if (context.DryRun)
            {
                return ActivityResult.Simulated($"Would react '{reaction}' to a recent Teams message.");
            }

            // setReaction support has historically been patchy across Teams surfaces; expected 4xx is skipped.
            await graph.PostAsync(
                intent.Actor.UserPrincipalName,
                target.Value.SetReactionPath,
                new { reactionType = reaction },
                cancellationToken).ConfigureAwait(false);

            return ActivityResult.Executed(detail: $"Reacted '{reaction}' to Teams message {target.Value.MessageId}.");
        }
        catch (UserNotEnrolledException ex)
        {
            return ActivityResult.Skipped(ex.Message);
        }
        catch (GraphException ex) when (ex.IsForbidden || ex.IsNotFound || IsExpectedReaction4xx(ex))
        {
            return ActivityResult.Skipped(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to react to Teams message for {IntentId}.", intent.Id);
            return ActivityResult.Failed(ex.Message);
        }
    }

    private async Task<ReactionTarget?> FindReactionTargetAsync(
        ActivityIntent intent,
        ExecContext context,
        CancellationToken ct)
    {
        var upn = intent.Actor.UserPrincipalName;
        var chats = (await graph.GetAsync(upn, $"users/{upn}/chats?$filter=chatType eq 'oneOnOne'&$top=10", ct).ConfigureAwait(false)).GetValueArray();
        foreach (var chat in chats)
        {
            var chatId = chat.GetStringOrNull("id");
            if (string.IsNullOrWhiteSpace(chatId))
            {
                continue;
            }

            var messages = (await graph.GetAsync(upn, $"chats/{chatId}/messages?$top=10", ct).ConfigureAwait(false)).GetValueArray();
            var message = messages.FirstOrDefault(m => IsReactable(m, intent.Actor.Id));
            var messageId = message.GetStringOrNull("id");
            if (!string.IsNullOrWhiteSpace(messageId))
            {
                return new ReactionTarget(messageId, $"chats/{chatId}/messages/{messageId}/setReaction");
            }
        }

        var channelTarget = await TeamsHelpers.FindChannelMessageAsync(graph, options, intent, context, ct).ConfigureAwait(false);
        return channelTarget is null
            ? null
            : new ReactionTarget(
                channelTarget.Value.MessageId,
                $"teams/{channelTarget.Value.TeamId}/channels/{channelTarget.Value.ChannelId}/messages/{channelTarget.Value.MessageId}/setReaction");
    }

    private static bool IsReactable(JsonElement message, string actorId) =>
        message.StringPropertyEquals("messageType", "message") &&
        !string.IsNullOrWhiteSpace(message.GetStringOrNull("id")) &&
        !string.IsNullOrWhiteSpace(message.GetNestedString("body", "content")) &&
        !string.Equals(message.GetNestedString("from", "user", "id"), actorId, StringComparison.OrdinalIgnoreCase);

    private static string PickReaction(ExecContext context, string intentId)
    {
        var rng = ExecutorHelpers.RandomFor(context.Seed, intentId);
        var roll = rng.NextDouble();
        return roll switch
        {
            < 0.65d => "like",
            < 0.85d => "heart",
            _ => "laugh"
        };
    }

    private static bool IsExpectedReaction4xx(GraphException ex) => (int)ex.StatusCode is >= 400 and < 500;
}

internal static class TeamsHelpers
{
    public static async Task<TeamChannel?> PickTeamChannelAsync(
        IGraphClient graph,
        TenantPulseOptions options,
        ActivityIntent intent,
        ExecContext context,
        CancellationToken ct)
    {
        var upn = intent.Actor.UserPrincipalName;
        var teams = (await graph.GetAsync(upn, $"users/{upn}/joinedTeams", ct).ConfigureAwait(false)).GetValueArray();
        if (options.Workloads.TeamNames.Count > 0)
        {
            teams = [.. teams.Where(t => options.Workloads.TeamNames.Contains(t.GetStringOrNull("displayName") ?? string.Empty, StringComparer.OrdinalIgnoreCase))];
        }

        var rng = ExecutorHelpers.RandomFor(context.Seed, intent.Id);
        foreach (var team in teams.OrderBy(_ => rng.Next()))
        {
            var teamId = team.GetStringOrNull("id");
            if (string.IsNullOrWhiteSpace(teamId))
            {
                continue;
            }

            var channels = (await graph.GetAsync(upn, $"teams/{teamId}/channels", ct).ConfigureAwait(false))
                .GetValueArray()
                .Where(c => c.StringPropertyEquals("membershipType", "standard"))
                .OrderBy(c => c.StringPropertyEquals("displayName", "General") ? 1 : 0)
                .ThenBy(_ => rng.Next())
                .ToArray();

            var channel = channels.FirstOrDefault();
            var channelId = channel.GetStringOrNull("id");
            if (!string.IsNullOrWhiteSpace(channelId))
            {
                // Private/shared channels frequently 403 with delegated demo users; standard channels are reliable.
                return new TeamChannel(teamId, team.GetStringOrNull("displayName") ?? teamId, channelId, channel.GetStringOrNull("displayName") ?? channelId);
            }
        }

        return null;
    }

    public static async Task<ChannelMessageTarget?> FindChannelMessageAsync(
        IGraphClient graph,
        TenantPulseOptions options,
        ActivityIntent intent,
        ExecContext context,
        CancellationToken ct)
    {
        var channel = await PickTeamChannelAsync(graph, options, intent, context, ct).ConfigureAwait(false);
        if (channel is null)
        {
            return null;
        }

        var messages = (await graph.GetAsync(
            intent.Actor.UserPrincipalName,
            $"teams/{channel.Value.TeamId}/channels/{channel.Value.ChannelId}/messages?$top=20",
            ct).ConfigureAwait(false)).GetValueArray();

        foreach (var message in messages)
        {
            var messageId = message.GetStringOrNull("id");
            var body = message.GetNestedString("body", "content");
            var authorId = message.GetNestedString("from", "user", "id");
            if (string.IsNullOrWhiteSpace(messageId) ||
                string.IsNullOrWhiteSpace(body) ||
                !message.StringPropertyEquals("messageType", "message") ||
                string.Equals(authorId, intent.Actor.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new ChannelMessageTarget(
                channel.Value.TeamId,
                channel.Value.TeamName,
                channel.Value.ChannelId,
                channel.Value.ChannelName,
                messageId,
                message.GetStringOrNull("subject"),
                body);
        }

        return null;
    }
}

internal readonly record struct TeamChannel(string TeamId, string TeamName, string ChannelId, string ChannelName);

internal readonly record struct ChannelMessageTarget(
    string TeamId,
    string TeamName,
    string ChannelId,
    string ChannelName,
    string MessageId,
    string? Subject,
    string BodyPreview);

internal readonly record struct ReactionTarget(string MessageId, string SetReactionPath);

internal static class TeamsPayloads
{
    public static Dictionary<string, object> Member(string userId, string graphRoot) => new(StringComparer.Ordinal)
    {
        ["@odata.type"] = "#microsoft.graph.aadUserConversationMember",
        ["roles"] = new[] { "owner" },
        ["user@odata.bind"] = $"{graphRoot}/v1.0/users('{userId}')"
    };
}

