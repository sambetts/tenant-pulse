using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Activities;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Content;
using TenantPulse.Core.Personas;
using TenantPulse.Engine.Graph;
using ExecContext = TenantPulse.Core.Activities.ExecutionContext;

namespace TenantPulse.Engine.Activities;

public sealed class CreateDocumentExecutor(
    IGraphClient graph,
    IContentGenerator contentGenerator,
    TenantPulseOptions options,
    ILogger<CreateDocumentExecutor> logger) : IActivityExecutor
{
    public ActivityKind Kind => ActivityKind.CreateDocument;

    public async Task<ActivityResult> ExecuteAsync(
        ActivityIntent intent,
        ExecContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await contentGenerator.GenerateAsync(
                new ContentRequest { Shape = ContentShape.DocumentBody, Intent = intent },
                cancellationToken).ConfigureAwait(false);
            var title = ExecutorHelpers.SubjectOrFallback(content.Subject, intent.Topic);
            var fileName = BuildFileName(intent, title);
            var target = await ResolveUploadTargetAsync(intent, context, fileName, cancellationToken).ConfigureAwait(false);

            if (context.DryRun)
            {
                return ActivityResult.Simulated($"Would create '{fileName}' in {target.Description}.");
            }

            var paragraphs = SplitParagraphs(content.Body);
            var bytes = DocxBuilder.Create(title, paragraphs);
            var item = await graph.PutContentAsync(
                intent.Actor.UserPrincipalName,
                target.ContentPath,
                bytes,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                cancellationToken).ConfigureAwait(false);

            var itemId = item.GetStringOrNull("id");
            var driveId = item.GetNestedString("parentReference", "driveId") ?? target.DriveId;
            return ActivityResult.Executed(
                itemId,
                itemId is null || driveId is null ? null : $"drives/{driveId}/items/{itemId}",
                $"Created '{fileName}' in {target.Description}.");
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
            logger.LogError(ex, "Failed to create document for {IntentId}.", intent.Id);
            return ActivityResult.Failed(ex.Message);
        }
    }

    private async Task<UploadTarget> ResolveUploadTargetAsync(
        ActivityIntent intent,
        ExecContext context,
        string fileName,
        CancellationToken ct)
    {
        var rng = ExecutorHelpers.RandomFor(context.Seed, intent.Id);
        if (options.Workloads.SharePointSites.Count > 0 && rng.Next(2) is 0)
        {
            var sitePath = options.Workloads.SharePointSites[rng.Next(options.Workloads.SharePointSites.Count)];
            var target = await TryResolveSharePointTargetAsync(intent.Actor.UserPrincipalName, sitePath, fileName, ct).ConfigureAwait(false);
            if (target is not null)
            {
                return target.Value;
            }
        }

        var storyline = ExecutorHelpers.SafeFileSegment(intent.StorylineId ?? intent.Topic);
        return new UploadTarget(
            $"users/{intent.Actor.UserPrincipalName}/drive/root:/Documents/{storyline} - {fileName}:/content",
            DriveId: null,
            "actor OneDrive Documents");
    }

    private async Task<UploadTarget?> TryResolveSharePointTargetAsync(string upn, string configuredSite, string fileName, CancellationToken ct)
    {
        try
        {
            var siteLookup = BuildSiteLookup(configuredSite);
            if (siteLookup is null)
            {
                return null;
            }

            var site = await graph.GetAsync(upn, siteLookup, ct).ConfigureAwait(false);
            var siteId = site.GetStringOrNull("id");
            if (string.IsNullOrWhiteSpace(siteId))
            {
                return null;
            }

            var drive = await graph.GetAsync(upn, $"sites/{siteId}/drive", ct).ConfigureAwait(false);
            var driveId = drive.GetStringOrNull("id");
            return string.IsNullOrWhiteSpace(driveId)
                ? null
                : new UploadTarget($"drives/{driveId}/root:/Shared Documents/{fileName}:/content", driveId, $"SharePoint site {configuredSite}");
        }
        catch (GraphException ex) when (ex.IsForbidden || ex.IsNotFound)
        {
            return null;
        }
    }

    private static string? BuildSiteLookup(string configuredSite)
    {
        if (Uri.TryCreate(configuredSite, UriKind.Absolute, out var uri))
        {
            return $"sites/{uri.Host}:{uri.AbsolutePath}";
        }

        if (configuredSite.Contains(':', StringComparison.Ordinal))
        {
            return $"sites/{configuredSite}";
        }

        return configuredSite.StartsWith("/", StringComparison.Ordinal)
            ? $"sites/root:{configuredSite}"
            : null;
    }

    private static string BuildFileName(ActivityIntent intent, string title)
    {
        var hinted = intent.Hint("fileName");
        var baseName = ExecutorHelpers.SafeFileSegment(string.IsNullOrWhiteSpace(hinted) ? title : hinted);
        return baseName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ? baseName : $"{baseName}.docx";
    }

    private static IReadOnlyList<string> SplitParagraphs(string body) =>
        [.. body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !string.IsNullOrWhiteSpace(p))];
}

public sealed class EditDocumentExecutor(
    IGraphClient graph,
    IContentGenerator contentGenerator,
    ILogger<EditDocumentExecutor> logger) : IActivityExecutor
{
    public ActivityKind Kind => ActivityKind.EditDocument;

    public async Task<ActivityResult> ExecuteAsync(
        ActivityIntent intent,
        ExecContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = await FindEditableDocumentAsync(intent.Actor.UserPrincipalName, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return ActivityResult.Skipped("No editable .docx file was found in the actor's recent files.");
            }

            var content = await contentGenerator.GenerateAsync(
                new ContentRequest
                {
                    Shape = ContentShape.DocumentBody,
                    Intent = intent,
                    Context = [document.Value.Name]
                },
                cancellationToken).ConfigureAwait(false);
            var updateParagraph = $"{DateTimeOffset.UtcNow:yyyy-MM-dd}: {content.Body.Trim()}";

            if (context.DryRun)
            {
                return ActivityResult.Simulated($"Would append an update to '{document.Value.Name}'.");
            }

            var bytes = await graph.GetContentAsync(
                intent.Actor.UserPrincipalName,
                $"drives/{document.Value.DriveId}/items/{document.Value.ItemId}/content",
                cancellationToken).ConfigureAwait(false);
            var updated = DocxBuilder.AppendParagraph(bytes, updateParagraph);
            var item = await graph.PutContentAsync(
                intent.Actor.UserPrincipalName,
                $"drives/{document.Value.DriveId}/items/{document.Value.ItemId}/content",
                updated,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                cancellationToken).ConfigureAwait(false);

            var itemId = item.GetStringOrNull("id") ?? document.Value.ItemId;
            return ActivityResult.Executed(
                itemId,
                $"drives/{document.Value.DriveId}/items/{itemId}",
                $"Appended an update to '{document.Value.Name}'.");
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
            logger.LogError(ex, "Failed to edit document for {IntentId}.", intent.Id);
            return ActivityResult.Failed(ex.Message);
        }
    }

    private async Task<EditableDocument?> FindEditableDocumentAsync(string upn, CancellationToken ct)
    {
        var recent = await graph.GetAsync(upn, $"users/{upn}/drive/recent", ct).ConfigureAwait(false);
        var document = recent.GetValueArray().Select(ParseEditableDocument).FirstOrDefault(d => d is not null);
        if (document is not null)
        {
            return document;
        }

        var root = await graph.GetAsync(upn, $"users/{upn}/drive/root/children", ct).ConfigureAwait(false);
        return root.GetValueArray().Select(ParseEditableDocument).FirstOrDefault(d => d is not null);
    }

    private static EditableDocument? ParseEditableDocument(JsonElement item)
    {
        var name = item.GetStringOrNull("name");
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var remote = item.ValueKind is JsonValueKind.Object && item.TryGetProperty("remoteItem", out var remoteItem)
            ? remoteItem
            : item;
        var itemId = remote.GetStringOrNull("id") ?? item.GetStringOrNull("id");
        var driveId = remote.GetNestedString("parentReference", "driveId") ?? item.GetNestedString("parentReference", "driveId");

        return string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(driveId)
            ? null
            : new EditableDocument(itemId, driveId, name);
    }
}

internal readonly record struct UploadTarget(string ContentPath, string? DriveId, string Description);

internal readonly record struct EditableDocument(string ItemId, string DriveId, string Name);

internal static class DocxBuilder
{
    public static byte[] Create(string title, IReadOnlyList<string> paragraphs)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body ?? throw new InvalidOperationException("Document body was not created.");
            body.Append(CreateParagraph(title, bold: true));
            foreach (var paragraph in paragraphs)
            {
                body.Append(CreateParagraph(paragraph, bold: false));
            }
        }

        return stream.ToArray();
    }

    public static byte[] AppendParagraph(byte[] existingDocument, string paragraph)
    {
        using var stream = new MemoryStream();
        stream.Write(existingDocument, 0, existingDocument.Length);
        stream.Position = 0;
        using (var document = WordprocessingDocument.Open(stream, isEditable: true))
        {
            var mainPart = document.MainDocumentPart ?? document.AddMainDocumentPart();
            mainPart.Document ??= new Document(new Body());
            var body = mainPart.Document.Body ?? mainPart.Document.AppendChild(new Body());
            body.Append(CreateParagraph(paragraph, bold: false));
            mainPart.Document.Save();
        }

        return stream.ToArray();
    }

    private static Paragraph CreateParagraph(string text, bool bold)
    {
        var runProperties = bold ? new RunProperties(new Bold()) : null;
        var run = new Run();
        if (runProperties is not null)
        {
            run.Append(runProperties);
        }

        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new Paragraph(run);
    }
}



internal static class GraphContentExtensions
{
    public static Task<byte[]> GetContentAsync(
        this IGraphClient graph,
        string upn,
        string path,
        CancellationToken cancellationToken,
        bool beta = false) =>
        graph is GraphClient concrete
            ? concrete.GetContentAsync(upn, path, cancellationToken, beta)
            : throw new NotSupportedException("Binary content download requires the TenantPulse GraphClient implementation.");
}
