using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using System.ClientModel;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Content;

namespace TenantPulse.Engine.Content;

public sealed partial class AzureOpenAIContentGenerator : IContentGenerator
{
    private readonly ChatClient _chatClient;
    private readonly ContentPromptBuilder _promptBuilder;
    private readonly TenantPulseOptions _options;
    private readonly ILogger<AzureOpenAIContentGenerator> _logger;

    public AzureOpenAIContentGenerator(
        TenantPulseOptions options,
        ContentPromptBuilder promptBuilder,
        ILogger<AzureOpenAIContentGenerator> logger)
    {
        _options = options;
        _promptBuilder = promptBuilder;
        _logger = logger;

        var endpoint = options.Content.Endpoint;
        var deployment = options.Content.Deployment;
        var apiKey = string.IsNullOrWhiteSpace(options.Content.ApiKey)
            ? Environment.GetEnvironmentVariable("TENANTPULSE_AOAI_KEY")
            : options.Content.ApiKey;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("Azure OpenAI content generation requires TenantPulse:Content:Endpoint.");
        }

        if (string.IsNullOrWhiteSpace(deployment))
        {
            throw new InvalidOperationException("Azure OpenAI content generation requires TenantPulse:Content:Deployment.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Azure OpenAI content generation requires TenantPulse:Content:ApiKey or the TENANTPULSE_AOAI_KEY environment variable.");
        }

        var client = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey));
        _chatClient = client.GetChatClient(deployment);
    }

    public async Task<GeneratedContent> GenerateAsync(ContentRequest request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Generating {Shape} content for {Topic}", request.Shape, request.Intent.Topic);

        var prompt = _promptBuilder.Build(request);
        var messages = new ChatMessage[]
        {
            ChatMessage.CreateSystemMessage(prompt.System),
            ChatMessage.CreateUserMessage(prompt.User)
        };

        var chatOptions = new ChatCompletionOptions
        {
            Temperature = (float)_options.Content.Temperature,
            MaxOutputTokenCount = _options.Content.MaxTokens,
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };

        var completion = await _chatClient.CompleteChatAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);
        var text = completion.Value.Content.Count == 0 ? string.Empty : completion.Value.Content[0].Text;

        return ParseResponse(text, request.Shape, request.Intent.Topic);
    }

    private static GeneratedContent ParseResponse(string responseText, ContentShape shape, string topic)
    {
        var cleaned = StripMarkdownFences(responseText).Trim();
        try
        {
            using var document = JsonDocument.Parse(cleaned);
            var root = document.RootElement;
            var subject = root.TryGetProperty("subject", out var subjectElement) && subjectElement.ValueKind != JsonValueKind.Null
                ? CleanValue(subjectElement.GetString())
                : null;
            var body = root.TryGetProperty("body", out var bodyElement)
                ? CleanValue(bodyElement.GetString())
                : cleaned;

            return new GeneratedContent
            {
                Subject = ShapeHasSubject(shape) ? CleanSubject(subject, topic) : null,
                Body = CleanBody(body),
                FromTemplate = false
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new GeneratedContent
            {
                Subject = ShapeHasSubject(shape) ? DeriveSubject(cleaned, topic) : null,
                Body = CleanBody(cleaned),
                FromTemplate = false
            };
        }
    }

    private static string CleanBody(string? value)
    {
        var cleaned = CleanValue(value);
        cleaned = LeadingSubjectLineRegex().Replace(cleaned, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Quick update on this — I'll share more shortly." : cleaned;
    }

    private static string? CleanSubject(string? value, string topic)
    {
        var cleaned = CleanValue(value);
        cleaned = LeadingSubjectPrefixRegex().Replace(cleaned, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? DeriveSubject(string.Empty, topic) : cleaned;
    }

    private static string CleanValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = StripMarkdownFences(value).Trim();
        if (cleaned.Length >= 2 && ((cleaned[0] == '"' && cleaned[^1] == '"') || (cleaned[0] == '\'' && cleaned[^1] == '\'')))
        {
            cleaned = cleaned[1..^1].Trim();
        }

        return cleaned.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }

    private static string StripMarkdownFences(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return value;
        }

        var firstLineEnd = trimmed.IndexOf('\n', StringComparison.Ordinal);
        if (firstLineEnd < 0)
        {
            return value;
        }

        var withoutOpening = trimmed[(firstLineEnd + 1)..];
        var closingIndex = withoutOpening.LastIndexOf("```", StringComparison.Ordinal);
        return closingIndex >= 0 ? withoutOpening[..closingIndex] : withoutOpening;
    }

    private static string? DeriveSubject(string text, string topic)
    {
        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var subject = string.IsNullOrWhiteSpace(firstLine) ? topic : firstLine;
        subject = LeadingSubjectPrefixRegex().Replace(subject, string.Empty).Trim();
        return subject.Length > 80 ? subject[..80].TrimEnd() : subject;
    }

    private static bool ShapeHasSubject(ContentShape shape) =>
        shape is ContentShape.EmailNew or ContentShape.TeamsChannelPost or ContentShape.DocumentBody or ContentShape.MeetingInvite;

    [GeneratedRegex(@"^\s*Subject:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingSubjectPrefixRegex();

    [GeneratedRegex(@"^\s*Subject:\s*.*(?:\n|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingSubjectLineRegex();
}
