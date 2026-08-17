using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TenantPulse.Core.Configuration;
using TenantPulse.Engine.Content;

namespace TenantPulse.Tests.Content;

/// <summary>
/// Credential selection for Azure OpenAI. Governed subscriptions routinely set
/// <c>disableLocalAuth</c> on the resource, which rejects every API key with
/// <c>403 AuthenticationTypeDisabled</c>. Content generation falls back to templates on failure, so
/// picking the wrong credential produces no error at all — only quietly blander content — which is
/// exactly why this needs pinning down in tests.
/// </summary>
public class AzureOpenAIContentGeneratorTests : IDisposable
{
    private const string KeyVariable = "TENANTPULSE_AOAI_KEY";

    private readonly string? _originalKeyVariable = Environment.GetEnvironmentVariable(KeyVariable);

    // The generator falls back to the environment for a key, so a developer machine that has one
    // set would otherwise flip the "no key" case to key auth and fail only for some people.
    public AzureOpenAIContentGeneratorTests() =>
        Environment.SetEnvironmentVariable(KeyVariable, null);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KeyVariable, _originalKeyVariable);
        GC.SuppressFinalize(this);
    }

    private static AzureOpenAIContentGenerator Create(ContentOptions content)
    {
        var options = new TenantPulseOptions { Content = content };

        return new AzureOpenAIContentGenerator(
            options,
            new ContentPromptBuilder(options),
            NullLogger<AzureOpenAIContentGenerator>.Instance);
    }

    private static ContentOptions Configured() => new()
    {
        Provider = ContentProvider.AzureOpenAI,
        Endpoint = "https://example.openai.azure.com/",
        Deployment = "gpt-4.1-mini"
    };

    [Fact]
    public void An_absent_key_selects_Entra_rather_than_failing()
    {
        var options = Configured();
        options.ApiKey = null;

        Create(options).AuthenticationMode.Should().Be("Entra");
    }

    [Fact]
    public void A_key_is_used_when_one_is_configured()
    {
        var options = Configured();
        options.ApiKey = "a-key";

        Create(options).AuthenticationMode.Should().Be("API key");
    }

    [Fact]
    public void UseEntraAuth_wins_over_a_configured_key()
    {
        var options = Configured();
        options.ApiKey = "a-key";
        options.UseEntraAuth = true;

        Create(options).AuthenticationMode.Should().Be("Entra");
    }

    [Fact]
    public void An_endpoint_is_still_required()
    {
        var options = Configured();
        options.Endpoint = null;

        var act = () => Create(options);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Endpoint*");
    }

    [Fact]
    public void A_deployment_is_still_required()
    {
        var options = Configured();
        options.Deployment = null;

        var act = () => Create(options);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Deployment*");
    }
}
