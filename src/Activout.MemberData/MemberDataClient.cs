using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Activout.MemberData;

/// <summary>
/// Thin client for LinkedIn's Member Data Portability (Member) API: Member Snapshot, Member Changelog and member
/// authorisation. Responses are kept as raw JSON so unknown domains and fields survive. Pass an HttpClient built on
/// <see cref="CreateResilienceHandler"/> (as <see cref="Create"/> does) to retry transient failures.
/// </summary>
public sealed class MemberDataClient(HttpClient http, string accessToken)
{
    public const string DefaultBaseUrl = "https://api.linkedin.com/rest/";

    /// <summary>The only version the snapshot endpoint accepts (others fail with 426 NONEXISTENT_VERSION).</summary>
    public const string LinkedinVersion = "202312";

    /// <summary>LinkedIn recommends 10 changelog events per request; 50 is the maximum.</summary>
    public const int DefaultChangelogCount = 10;

    public const int MaxChangelogCount = 50;

    public static MemberDataClient Create(string accessToken, HttpMessageHandler? handler = null,
        string baseUrl = DefaultBaseUrl) =>
        new(new HttpClient(CreateResilienceHandler(handler ?? new SocketsHttpHandler()))
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(100),
        }, accessToken);

    /// <summary>
    /// Retries network errors, 408, 429 and 5xx of GET requests, honouring Retry-After, with exponential backoff and
    /// jitter. 400/401/403/404 are never retried.
    /// </summary>
    public static DelegatingHandler CreateResilienceHandler(HttpMessageHandler inner, TimeSpan? baseDelay = null)
    {
        var retry = new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 4,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = baseDelay ?? TimeSpan.FromSeconds(2),
            MaxDelay = TimeSpan.FromSeconds(60),
        };
        retry.DisableForUnsafeHttpMethods();
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>().AddRetry(retry).Build();
        return new ResilienceHandler(pipeline) { InnerHandler = inner };
    }

    /// <summary>The DMA authorisation for the token's member, or null when LinkedIn reports none.</summary>
    public async Task<MemberAuthorization?> GetAuthorization(CancellationToken ct)
    {
        using var document = await Get("memberAuthorizations?q=memberAndApplication", ct);
        if (!document.RootElement.TryGetProperty("elements", out var elements) || elements.GetArrayLength() == 0)
            return null;

        var element = elements[0];
        var key = element.GetProperty("memberComplianceAuthorizationKey");
        return new MemberAuthorization(
            key.GetProperty("member").GetString()!,
            key.GetProperty("developerApplication").GetString()!,
            element.TryGetProperty("regulatedAt", out var at) && at.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(at.GetInt64())
                : null,
            element.TryGetProperty("memberComplianceScopes", out var scopes)
                ? scopes.EnumerateArray().Select(s => s.GetString() ?? "").ToList()
                : []);
    }

    /// <summary>
    /// Registers the member for DMA data generation (Member Authorization Management API: POST with an empty object).
    /// LinkedIn registers members automatically when the token is generated; this is for debugging when no data
    /// appears after a few minutes. Never retried automatically.
    /// </summary>
    public async Task Register(CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, "memberAuthorizations");
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) await ThrowError(response, ct);
    }

    /// <summary>
    /// Every page of the snapshot, for one domain or (with <paramref name="domain"/> null) for all domains with data,
    /// in the order LinkedIn returns them. Paging follows the documentation: <c>start</c> is a page index, and the
    /// end is the 404 "No data found" response rather than <c>paging.total</c>. A domain without data yields nothing.
    /// Enumeration that completes without an exception has read the whole snapshot.
    /// </summary>
    public async IAsyncEnumerable<SnapshotPage> GetSnapshot(string? domain,
        [EnumeratorCancellation] CancellationToken ct)
    {
        for (var start = 0; ; start++)
        {
            var path = "memberSnapshotData?q=criteria" +
                       (domain == null ? "" : "&domain=" + Uri.EscapeDataString(domain)) +
                       "&start=" + start.ToString(CultureInfo.InvariantCulture);
            JsonDocument document;
            try
            {
                document = await Get(path, ct);
            }
            catch (MemberDataApiException e) when (e.IsNoData)
            {
                yield break;
            }

            using (document)
            {
                var root = document.RootElement;
                int? total = root.TryGetProperty("paging", out var paging) &&
                             paging.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number
                    ? t.GetInt32()
                    : null;
                if (!root.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
                    throw new MemberDataApiException(HttpStatusCode.OK, "Snapshot response has no elements array");

                // Documented as exactly one element per page; accept more rather than drop data.
                foreach (var element in elements.EnumerateArray())
                {
                    var pageDomain = element.TryGetProperty("snapshotDomain", out var d) ? d.GetString() : null;
                    if (string.IsNullOrEmpty(pageDomain))
                        throw new MemberDataApiException(HttpStatusCode.OK, "Snapshot element has no snapshotDomain");
                    var records = element.TryGetProperty("snapshotData", out var data) &&
                                  data.ValueKind == JsonValueKind.Array
                        ? data.EnumerateArray().Select(r => r.Clone()).ToList()
                        : [];
                    yield return new SnapshotPage(pageDomain, start, total, records);
                }
            }
        }
    }

    /// <summary>
    /// One page of changelog events with <c>processedAt</c> at or after <paramref name="startTime"/> (inclusive,
    /// epoch milliseconds). LinkedIn returns at most 28 days of events.
    /// </summary>
    public async Task<ChangelogPage> GetChangelog(long? startTime, int count, CancellationToken ct, int start = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxChangelogCount);
        var path = "memberChangeLogs?q=memberAndApplication&count=" + count.ToString(CultureInfo.InvariantCulture);
        if (startTime is { } t) path += "&startTime=" + t.ToString(CultureInfo.InvariantCulture);
        if (start > 0) path += "&start=" + start.ToString(CultureInfo.InvariantCulture);

        using var document = await Get(path, ct);
        var events = document.RootElement.TryGetProperty("elements", out var elements) &&
                     elements.ValueKind == JsonValueKind.Array
            ? elements.EnumerateArray().Select(e => e.Clone()).ToList()
            : [];
        return new ChangelogPage(events);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Linkedin-Version", LinkedinVersion);
        request.Headers.Add("X-Restli-Protocol-Version", "2.0.0");
        return request;
    }

    private async Task<JsonDocument> Get(string path, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) await ThrowError(response, ct);
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException)
        {
            throw new MemberDataApiException(response.StatusCode, "Response is not valid JSON");
        }
    }

    private static async Task ThrowError(HttpResponseMessage response, CancellationToken ct)
    {
        string? message = null;
        try
        {
            using var error = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),
                cancellationToken: ct);
            if (error.RootElement.ValueKind == JsonValueKind.Object &&
                error.RootElement.TryGetProperty("message", out var m))
                message = m.GetString();
        }
        catch (JsonException)
        {
            // Not a JSON error body; fall back to the status line without dumping the body.
        }

        throw new MemberDataApiException(response.StatusCode,
            string.IsNullOrEmpty(message) ? response.ReasonPhrase ?? "Request failed" : message);
    }
}
