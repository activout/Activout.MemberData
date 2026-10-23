using System.Net;
using RichardSzalay.MockHttp;

namespace Activout.MemberData.Tests;

public class MemberDataClientTests : IDisposable
{
    private const string Api = "https://api.test/rest/";
    private const string Token = "synthetic-token-value";
    private readonly MockHttpMessageHandler _http = new();
    private readonly MemberDataClient _client;

    public MemberDataClientTests()
    {
        _client = new MemberDataClient(
            new HttpClient(MemberDataClient.CreateResilienceHandler(_http, TimeSpan.FromMilliseconds(1)))
            {
                BaseAddress = new Uri(Api),
            }, Token);
    }

    public void Dispose() => _http.Dispose();

    private async Task<List<SnapshotPage>> ReadAll(string? domain)
    {
        var pages = new List<SnapshotPage>();
        await foreach (var page in _client.GetSnapshot(domain, CancellationToken.None)) pages.Add(page);
        return pages;
    }

    private void AllDomainPages()
    {
        for (var i = 0; i < 4; i++)
            _http.When(Api + "memberSnapshotData").WithExactQueryString($"q=criteria&start={i}")
                .Respond("application/json", Fixtures.Read($"snapshot-all-{i}.json"));
        _http.When(Api + "memberSnapshotData").WithExactQueryString("q=criteria&start=4")
            .Respond(HttpStatusCode.NotFound, "application/json", Fixtures.Read("snapshot-no-data.json"));
    }

    [Fact]
    public async Task Snapshot_pages_until_no_data_and_keeps_raw_records()
    {
        AllDomainPages();

        var pages = await ReadAll(null);

        Assert.Equal(["CONNECTIONS", "INBOX", "Events", "WHATSAPP_NUMBERS"], pages.Select(p => p.Domain));
        Assert.Equal([0, 1, 2, 3], pages.Select(p => p.Start));
        Assert.Equal(3, pages[0].Records.Count);
        Assert.Equal("Fictional Labs", pages[0].Records[2].GetProperty("Company").GetString());
        Assert.Equal(4, pages[0].Total);
    }

    [Fact]
    public async Task Snapshot_ignores_inaccurate_total_and_reads_past_it()
    {
        _http.When(Api + "memberSnapshotData").WithQueryString("start", "0")
            .Respond("application/json", """{"paging":{"total":1},"elements":[{"snapshotDomain":"INBOX","snapshotData":[{"a":"1"}]}]}""");
        _http.When(Api + "memberSnapshotData").WithQueryString("start", "1")
            .Respond("application/json", """{"paging":{"total":1},"elements":[{"snapshotDomain":"INBOX","snapshotData":[]}]}""");
        _http.When(Api + "memberSnapshotData").WithQueryString("start", "2")
            .Respond("application/json", """{"paging":{"total":1},"elements":[{"snapshotDomain":"INBOX","snapshotData":[{"a":"2"}]}]}""");
        _http.When(Api + "memberSnapshotData").WithQueryString("start", "3")
            .Respond(HttpStatusCode.NotFound, "application/json", Fixtures.Read("snapshot-no-data.json"));

        var pages = await ReadAll("INBOX");

        Assert.Equal([1, 0, 1], pages.Select(p => p.Records.Count));
    }

    [Fact]
    public async Task Empty_domain_yields_nothing()
    {
        _http.When(Api + "memberSnapshotData").WithExactQueryString("q=criteria&domain=PATENTS&start=0")
            .Respond(HttpStatusCode.NotFound, "application/json",
                """{"code":"","message":"No data found for this domain and memberId.","status":404}""");

        Assert.Empty(await ReadAll("PATENTS"));
    }

    [Fact]
    public async Task Unsupported_domain_is_reported_not_retried()
    {
        var request = _http.When(Api + "memberSnapshotData")
            .Respond(HttpStatusCode.BadRequest, "application/json", Fixtures.Read("snapshot-unsupported.json"));

        var e = await Assert.ThrowsAsync<MemberDataApiException>(() => ReadAll("LOGIN"));

        Assert.True(e.IsUnsupportedDomain);
        Assert.Equal(1, _http.GetMatchCount(request));
    }

    [Fact]
    public async Task Sends_version_and_bearer_headers()
    {
        _http.Expect(Api + "memberAuthorizations")
            .WithHeaders("Linkedin-Version", MemberDataClient.LinkedinVersion)
            .WithHeaders("Authorization", "Bearer " + Token)
            .Respond("application/json", Fixtures.Read("member-authorization.json"));

        var authorization = await _client.GetAuthorization(CancellationToken.None);

        _http.VerifyNoOutstandingExpectation();
        Assert.Equal("urn:li:person:SyntheticA1", authorization!.Member);
        Assert.Equal("urn:li:developerApplication:100000001", authorization.DeveloperApplication);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792700000000), authorization.RegulatedAt);
        Assert.Equal(["DMA"], authorization.Scopes);
    }

    [Fact]
    public async Task No_authorization_returns_null()
    {
        _http.When(Api + "memberAuthorizations").Respond("application/json", """{"paging":{},"elements":[]}""");

        Assert.Null(await _client.GetAuthorization(CancellationToken.None));
    }

    [Fact]
    public async Task Register_posts_an_empty_object_once()
    {
        var request = _http.When(HttpMethod.Post, Api + "memberAuthorizations").WithContent("{}")
            .Respond(HttpStatusCode.Created);

        await _client.Register(CancellationToken.None);

        Assert.Equal(1, _http.GetMatchCount(request));
    }

    [Fact]
    public async Task Register_is_never_retried()
    {
        var request = _http.When(HttpMethod.Post, Api + "memberAuthorizations").Respond(HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<MemberDataApiException>(() => _client.Register(CancellationToken.None));

        Assert.Equal(1, _http.GetMatchCount(request));
    }

    [Fact]
    public async Task Unauthorised_is_typed_and_not_retried()
    {
        var request = _http.When(Api + "memberAuthorizations").Respond(HttpStatusCode.Unauthorized, "application/json",
            """{"serviceErrorCode":65601,"message":"The token used in the request has been revoked by the user","status":401}""");

        var e = await Assert.ThrowsAsync<MemberDataApiException>(() => _client.GetAuthorization(CancellationToken.None));

        Assert.True(e.IsAuthentication);
        Assert.Contains("revoked", e.Message);
        Assert.DoesNotContain(Token, e.Message);
        Assert.Equal(1, _http.GetMatchCount(request));
    }

    [Fact]
    public async Task Forbidden_is_a_permission_error()
    {
        _http.When(Api + "memberChangeLogs").Respond(HttpStatusCode.Forbidden, "application/json",
            """{"message":"Not enough permissions to access: memberChangeLogs.FINDER-memberAndApplication","status":403}""");

        var e = await Assert.ThrowsAsync<MemberDataApiException>(() => _client.GetChangelog(null, 10, CancellationToken.None));

        Assert.True(e.IsPermission);
    }

    [Fact]
    public async Task Rate_limit_and_server_errors_are_retried()
    {
        var calls = 0;
        _http.When(Api + "memberChangeLogs").Respond(_ => ++calls switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.Zero) } },
            2 => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixtures.Read("changelog.json")) },
        });

        var page = await _client.GetChangelog(null, 10, CancellationToken.None);

        Assert.Equal(3, calls);
        Assert.Equal(5, page.Events.Count);
    }

    [Fact]
    public async Task Changelog_sends_start_time_and_count()
    {
        _http.Expect(Api + "memberChangeLogs")
            .WithExactQueryString("q=memberAndApplication&count=10&startTime=1792700000000")
            .Respond("application/json", Fixtures.Read("changelog.json"));

        var page = await _client.GetChangelog(1792700000000, 10, CancellationToken.None);

        _http.VerifyNoOutstandingExpectation();
        Assert.Equal(9000000001, page.Events[0].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Changelog_count_is_bounded()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.GetChangelog(null, 51, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.GetChangelog(null, 0, CancellationToken.None));
    }

    [Fact]
    public async Task Corrupt_json_is_an_api_error()
    {
        _http.When(Api + "memberSnapshotData").Respond("application/json", "{not json");

        var e = await Assert.ThrowsAsync<MemberDataApiException>(() => ReadAll(null));

        Assert.Contains("not valid JSON", e.Message);
    }

    [Fact]
    public async Task Cancellation_stops_paging()
    {
        AllDomainPages();
        using var cts = new CancellationTokenSource();
        var pages = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in _client.GetSnapshot(null, cts.Token))
            {
                pages++;
                await cts.CancelAsync();
            }
        });

        Assert.Equal(1, pages);
    }
}
