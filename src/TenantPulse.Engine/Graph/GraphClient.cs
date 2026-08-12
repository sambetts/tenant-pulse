using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TenantPulse.Core.Configuration;
using TenantPulse.Core.Personas;

namespace TenantPulse.Engine.Graph;

public interface IGraphClient
{
    Task<JsonElement> GetAsync(string upn, string path, CancellationToken cancellationToken, bool beta = false);

    Task<JsonElement?> PostAsync(string upn, string path, object body, CancellationToken cancellationToken, bool beta = false);

    Task PatchAsync(string upn, string path, object body, CancellationToken cancellationToken, bool beta = false);

    Task DeleteAsync(string upn, string path, CancellationToken cancellationToken, bool beta = false);

    Task<JsonElement> PutContentAsync(string upn, string path, byte[] content, string contentType, CancellationToken cancellationToken, bool beta = false);

    Task<IReadOnlyList<JsonElement>> GetPagedAsync(string upn, string path, int maxItems, CancellationToken cancellationToken, bool beta = false);

    Task<JsonElement> GetWithTokenAsync(string path, string accessToken, CancellationToken cancellationToken, bool beta = false);
}

public sealed class GraphException(HttpStatusCode statusCode, string path, string responseBody)
    : Exception($"Microsoft Graph request '{path}' failed with {(int)statusCode} {statusCode}: {responseBody}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string Path { get; } = path;

    public string ResponseBody { get; } = responseBody;

    public bool IsNotFound => StatusCode is HttpStatusCode.NotFound;

    public bool IsForbidden => StatusCode is HttpStatusCode.Forbidden;
}

public sealed class GraphClient(
    HttpClient httpClient,
    IUserTokenProvider tokenProvider,
    TenantPulseOptions options,
    ILogger<GraphClient> logger) : IGraphClient
{
    private const int MaxRetries = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Task<JsonElement> GetAsync(string upn, string path, CancellationToken cancellationToken, bool beta = false) =>
        SendJsonAsync(upn, HttpMethod.Get, path, body: null, cancellationToken, beta);

    public async Task<IReadOnlyList<JsonElement>> GetPagedAsync(
        string upn,
        string path,
        int maxItems,
        CancellationToken cancellationToken,
        bool beta = false)
    {
        var items = new List<JsonElement>();
        var nextPath = path;
        var nextIsAbsolute = false;

        while (!string.IsNullOrWhiteSpace(nextPath) && items.Count < maxItems)
        {
            var page = await SendJsonAsync(upn, HttpMethod.Get, nextPath, body: null, cancellationToken, beta, nextIsAbsolute)
                .ConfigureAwait(false);

            if (page.TryGetProperty("value", out var value) && value.ValueKind is JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (items.Count >= maxItems)
                    {
                        break;
                    }

                    items.Add(item.Clone());
                }
            }

            if (items.Count >= maxItems ||
                !page.TryGetProperty("@odata.nextLink", out var nextLink) ||
                nextLink.ValueKind is not JsonValueKind.String)
            {
                break;
            }

            nextPath = nextLink.GetString() ?? string.Empty;
            nextIsAbsolute = Uri.TryCreate(nextPath, UriKind.Absolute, out _);
        }

        return items;
    }

    public Task<JsonElement?> PostAsync(string upn, string path, object body, CancellationToken cancellationToken, bool beta = false) =>
        SendOptionalJsonAsync(upn, HttpMethod.Post, path, body, cancellationToken, beta);

    public Task PatchAsync(string upn, string path, object body, CancellationToken cancellationToken, bool beta = false) =>
        SendNoContentAsync(upn, HttpMethod.Patch, path, body, cancellationToken, beta);

    public Task DeleteAsync(string upn, string path, CancellationToken cancellationToken, bool beta = false) =>
        SendNoContentAsync(upn, HttpMethod.Delete, path, body: null, cancellationToken, beta);

    public async Task<JsonElement> PutContentAsync(
        string upn,
        string path,
        byte[] content,
        string contentType,
        CancellationToken cancellationToken,
        bool beta = false)
    {
        using var response = await SendAsync(
            upn,
            HttpMethod.Put,
            path,
            () => new ByteArrayContent(content)
            {
                Headers = { ContentType = new MediaTypeHeaderValue(contentType) }
            },
            cancellationToken,
            beta,
            absolutePath: false).ConfigureAwait(false);

        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<byte[]> GetContentAsync(string upn, string path, CancellationToken cancellationToken, bool beta = false)
    {
        using var response = await SendAsync(
            upn,
            HttpMethod.Get,
            path,
            createContent: null,
            cancellationToken,
            beta,
            absolutePath: false).ConfigureAwait(false);

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }


    public async Task<JsonElement> GetWithTokenAsync(string path, string accessToken, CancellationToken cancellationToken, bool beta = false)
    {
        using var response = await SendAsync(
            upn: string.Empty,
            HttpMethod.Get,
            path,
            createContent: null,
            cancellationToken,
            beta,
            absolutePath: false,
            accessToken).ConfigureAwait(false);

        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> SendJsonAsync(
        string upn,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken,
        bool beta,
        bool absolutePath = false)
    {
        using var response = await SendAsync(upn, method, path, CreateJsonContentFactory(body), cancellationToken, beta, absolutePath)
            .ConfigureAwait(false);

        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement?> SendOptionalJsonAsync(
        string upn,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken,
        bool beta)
    {
        using var response = await SendAsync(upn, method, path, CreateJsonContentFactory(body), cancellationToken, beta, absolutePath: false)
            .ConfigureAwait(false);

        if (response.Content.Headers.ContentLength is 0)
        {
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private async Task SendNoContentAsync(
        string upn,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken,
        bool beta)
    {
        using var _ = await SendAsync(upn, method, path, CreateJsonContentFactory(body), cancellationToken, beta, absolutePath: false)
            .ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string upn,
        HttpMethod method,
        string path,
        Func<HttpContent?>? createContent,
        CancellationToken cancellationToken,
        bool beta,
        bool absolutePath,
        string? accessToken = null)
    {
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            using var request = new HttpRequestMessage(method, BuildUri(path, beta, absolutePath));
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                accessToken ?? await tokenProvider.GetAccessTokenAsync(upn, cancellationToken).ConfigureAwait(false));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = createContent?.Invoke();

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable &&
                attempt < MaxRetries)
            {
                var delay = GetRetryDelay(response, attempt);
                logger.LogWarning(
                    "Microsoft Graph throttled {Method} {Path} with {StatusCode}; retrying in {DelaySeconds}s (attempt {Attempt}/{MaxRetries}).",
                    method.Method,
                    path,
                    (int)response.StatusCode,
                    delay.TotalSeconds,
                    attempt + 1,
                    MaxRetries);

                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var statusCode = response.StatusCode;
            response.Dispose();
            throw new GraphException(statusCode, path, responseBody);
        }

        throw new GraphException(HttpStatusCode.ServiceUnavailable, path, "Retries exhausted.");
    }

    private Uri BuildUri(string path, bool beta, bool absolutePath)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri;
        }

        if (absolutePath)
        {
            return new Uri(path, UriKind.Absolute);
        }

        var baseUrl = options.Tenant.GraphBaseUrl.TrimEnd('/');
        var version = beta ? "beta" : "v1.0";
        return new Uri($"{baseUrl}/{version}/{path.TrimStart('/')}", UriKind.Absolute);
    }

    private static Func<HttpContent?>? CreateJsonContentFactory(object? body)
    {
        if (body is null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(body, JsonOptions);
        return () => new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            using var emptyDocument = JsonDocument.Parse("{}");
            return emptyDocument.RootElement.Clone();
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                return delay;
            }
        }

        return TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
    }
}

