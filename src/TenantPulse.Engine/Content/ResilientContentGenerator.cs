using Microsoft.Extensions.Logging;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Content;

namespace TenantPulse.Engine.Content;

public sealed class ResilientContentGenerator(
    IContentGenerator primary,
    TemplateContentGenerator fallback,
    TenantPulseOptions options,
    ILogger<ResilientContentGenerator> logger) : IContentGenerator
{
    public async Task<GeneratedContent> GenerateAsync(ContentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await primary.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (options.Content.FallbackToTemplates)
        {
            logger.LogWarning(ex, "Content generation failed for {Shape} on {Topic}; falling back to templates. {Message}", request.Shape, request.Intent.Topic, ex.Message);
            return await fallback.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
