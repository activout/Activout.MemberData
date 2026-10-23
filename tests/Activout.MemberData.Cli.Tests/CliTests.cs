using System.Net;
using System.Text.Json;
using RichardSzalay.MockHttp;

namespace Activout.MemberData.Cli.Tests;

/// <summary>Runs the real command line against a temporary database and a mocked API. Synthetic data only.</summary>
public class CliTests : IDisposable
{
    private const string Api = "https://api.test/rest/";
    private const string Token = "synthetic-secret-token-0123456789";
    private const long NewestProcessedAt = 1792700400000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "member-cli-test-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryCredentialStore _keychain = new();
    private readonly Dictionary<string, string> _envVars = new();
    private readonly ManualTime _time = new(DateTimeOffset.FromUnixTimeMilliseconds(NewestProcessedAt).AddHours(1));

    private string Database => Path.Combine(_dir, "member.db");

    public CliTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<(int ExitCode, JsonElement Json, string Stdout, string Stderr)> MemberWithStdin(
        MockHttpMessageHandler? http, string stdin, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await Program.Run([..args, "--database", Database], stdout, stderr, new StringReader(stdin),
            new CliEnvironment
            {
                Http = http, BaseUrl = Api, Keychain = _keychain, GetEnv = n => _envVars.GetValueOrDefault(n),
                OutputRedirected = true, InputRedirected = stdin.Length > 0, Time = _time, HomeDirectory = _dir,
            });
        var text = stdout.ToString();
        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain(Token, stderr.ToString());
        var json = text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return (exitCode, json, text, stderr.ToString());
    }

    private Task<(int ExitCode, JsonElement Json, string Stdout, string Stderr)> Member(MockHttpMessageHandler? http,
        params string[] args) => MemberWithStdin(http, "", args);

    private static MockHttpMessageHandler Api_(string member = "urn:li:person:SyntheticA1",
        Func<int, string?>? allPage = null, int pages = 4, string changelog = "changelog.json",
        Action<MockHttpMessageHandler>? first = null)
    {
        // MockHttp uses the first matching definition, so `first` overrides everything below.
        var http = new MockHttpMessageHandler();
        first?.Invoke(http);
        http.When(HttpMethod.Get, Api + "memberAuthorizations").Respond("application/json",
            Fixtures.Read("member-authorization.json").Replace("urn:li:person:SyntheticA1", member));
        http.When(Api + "memberChangeLogs").Respond("application/json", Fixtures.Read(changelog));
        for (var i = 0; i < pages; i++)
        {
            var body = allPage?.Invoke(i) ?? Fixtures.Read($"snapshot-all-{i}.json");
            http.When(Api + "memberSnapshotData").WithExactQueryString($"q=criteria&start={i}")
                .Respond("application/json", body);
        }

        http.When(Api + "memberSnapshotData").WithExactQueryString($"q=criteria&start={pages}")
            .Respond(HttpStatusCode.NotFound, "application/json", Fixtures.Read("snapshot-no-data.json"));
        foreach (var unsupported in new[] { "LOGIN", "EVENTS", "EASYAPPLY_BLOCKING" })
        {
            http.When(Api + "memberSnapshotData").WithQueryString("domain", unsupported)
                .Respond(HttpStatusCode.BadRequest, "application/json",
                    Fixtures.Read("snapshot-unsupported.json").Replace("LOGIN", unsupported));
        }

        http.Fallback.Respond(HttpStatusCode.NotFound, "application/json",
            """{"code":"","message":"No data found for this domain and memberId.","status":404}""");
        return http;
    }

    private void StoreToken() => _keychain.Set("access-token", JsonSerializer.Serialize(new { access_token = Token }));

    private static Dictionary<string, JsonElement> Domains(JsonElement sync) =>
        sync.GetProperty("result").GetProperty("snapshot").GetProperty("domains").EnumerateArray()
            .ToDictionary(d => d.GetProperty("domain").GetString()!);

    private async Task<long> Scalar(string sql)
    {
        var (exitCode, json, _, stderr) = await Member(null, "data", "query", sql);
        Assert.True(exitCode == 0, stderr);
        return json.GetProperty("result").GetProperty("rows")[0].EnumerateObject().First().Value.GetInt64();
    }

    // Credentials

    [Fact]
    public async Task Import_token_from_stdin_stores_it_and_never_prints_it()
    {
        var (exitCode, json, _, _) = await MemberWithStdin(Api_(), Token + "\n", "auth", "import-token", "--stdin", "--expires-in-days", "60");

        Assert.Equal(0, exitCode);
        Assert.Equal("urn:li:person:SyntheticA1", json.GetProperty("result").GetProperty("member").GetString());
        Assert.Contains(Token, _keychain.Get("access-token"));

        (exitCode, json, _, _) = await Member(null, "auth", "status");
        Assert.Equal(0, exitCode);
        var status = json.GetProperty("result");
        Assert.True(status.GetProperty("token_present").GetBoolean());
        Assert.Equal(_time.GetUtcNow().AddDays(60), status.GetProperty("expires_at").GetDateTimeOffset());
    }

    [Fact]
    public async Task Rejected_token_is_not_stored()
    {
        var http = new MockHttpMessageHandler();
        http.When(Api + "memberAuthorizations").Respond(HttpStatusCode.Unauthorized, "application/json",
            """{"message":"Invalid access token","status":401}""");

        var (exitCode, json, _, _) = await MemberWithStdin(http, Token, "auth", "import-token", "--stdin");

        Assert.Equal(3, exitCode);
        Assert.Equal("auth_required", json.GetProperty("error").GetProperty("type").GetString());
        Assert.Null(_keychain.Get("access-token"));
    }

    [Fact]
    public async Task Environment_token_is_ignored_unless_explicitly_selected()
    {
        _envVars["MEMBERDATA_ACCESS_TOKEN"] = Token;

        var (exitCode, json, _, _) = await Member(null, "auth", "status");
        Assert.Equal(3, exitCode);
        Assert.False(json.GetProperty("result").GetProperty("token_present").GetBoolean());

        (exitCode, json, _, _) = await Member(null, "auth", "status", "--credential-source", "environment");
        Assert.Equal(0, exitCode);
        Assert.True(json.GetProperty("result").GetProperty("token_present").GetBoolean());

        _envVars["MEMBERDATA_CREDENTIAL_SOURCE"] = "environment";
        (exitCode, _, _, _) = await Member(Api_(), "sync");
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Environment_source_is_read_only()
    {
        var (exitCode, _, _, _) = await MemberWithStdin(null, Token, "auth", "import-token", "--stdin", "--credential-source", "environment");

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Unavailable_keychain_never_falls_back_to_the_environment()
    {
        _keychain.Unavailable = true;
        _envVars["MEMBERDATA_ACCESS_TOKEN"] = Token;

        var (exitCode, json, _, _) = await Member(null, "auth", "status");
        Assert.Equal(3, exitCode);
        Assert.False(json.GetProperty("result").GetProperty("available").GetBoolean());

        (exitCode, _, _, _) = await Member(Api_(), "sync");
        Assert.Equal(3, exitCode);
    }

    [Fact]
    public async Task Expired_token_is_refused_before_calling_LinkedIn()
    {
        _keychain.Set("access-token", JsonSerializer.Serialize(new
        {
            access_token = Token, expires_at = _time.GetUtcNow().AddDays(-1),
        }));
        var http = new MockHttpMessageHandler();

        var (exitCode, json, _, _) = await Member(http, "sync");

        Assert.Equal(3, exitCode);
        Assert.Contains("expired", json.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Logout_removes_the_token_but_keeps_the_database()
    {
        StoreToken();
        await Member(Api_(), "sync");

        var (exitCode, json, _, _) = await Member(null, "auth", "logout");

        Assert.Equal(0, exitCode);
        Assert.True(json.GetProperty("result").GetProperty("removed").GetBoolean());
        Assert.Null(_keychain.Get("access-token"));
        Assert.True(File.Exists(Database));
    }

    [Fact]
    public async Task Revoked_token_during_sync_asks_for_a_new_token()
    {
        StoreToken();
        var http = new MockHttpMessageHandler();
        http.When(Api + "memberAuthorizations").Respond(HttpStatusCode.Unauthorized, "application/json",
            """{"message":"The token used in the request has been revoked by the user","status":401}""");

        var (exitCode, json, _, _) = await Member(http, "sync");

        Assert.Equal(3, exitCode);
        Assert.Contains("import-token", json.GetProperty("error").GetProperty("message").GetString());
    }

    // Registration

    [Fact]
    public async Task Registration_reports_not_registered_and_register_posts()
    {
        StoreToken();
        var http = new MockHttpMessageHandler();
        var registered = false;
        http.When(HttpMethod.Get, Api + "memberAuthorizations").Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(registered ? Fixtures.Read("member-authorization.json") : """{"elements":[]}"""),
        });
        http.When(HttpMethod.Post, Api + "memberAuthorizations").WithContent("{}").Respond(_ =>
        {
            registered = true;
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        var (exitCode, json, _, _) = await Member(http, "auth", "registration");
        Assert.Equal(0, exitCode);
        Assert.False(json.GetProperty("result").GetProperty("registered").GetBoolean());

        (exitCode, json, _, _) = await Member(http, "auth", "register");
        Assert.Equal(0, exitCode);
        Assert.True(json.GetProperty("result").GetProperty("registered").GetBoolean());
    }

    [Fact]
    public async Task Sync_without_registration_fails_with_guidance()
    {
        StoreToken();
        var http = new MockHttpMessageHandler();
        http.When(Api + "memberAuthorizations").Respond("application/json", """{"elements":[]}""");

        var (exitCode, json, _, _) = await Member(http, "sync");

        Assert.Equal(3, exitCode);
        Assert.Contains("member auth register", json.GetProperty("error").GetProperty("message").GetString());
    }

    // Synchronisation

    [Fact]
    public async Task Initial_sync_stores_every_domain_and_the_changelog()
    {
        StoreToken();

        var (exitCode, json, _, stderr) = await Member(Api_(), "sync");

        Assert.True(exitCode == 0, stderr);
        var result = json.GetProperty("result");
        Assert.Equal("initial", result.GetProperty("strategy").GetString());
        var domains = Domains(json);
        Assert.Equal(3, domains["CONNECTIONS"].GetProperty("records").GetInt32());
        Assert.Equal("synced", domains["Events"].GetProperty("status").GetString());
        Assert.Equal("synced", domains["WHATSAPP_NUMBERS"].GetProperty("status").GetString());
        Assert.Equal("unsupported", domains["LOGIN"].GetProperty("status").GetString());
        Assert.Equal("no_data", domains["PATENTS"].GetProperty("status").GetString());

        // Identical messages are kept as separate records.
        Assert.Equal(3, await Scalar("SELECT count(*) FROM snapshot_records WHERE domain = 'INBOX'"));
        Assert.Equal(1, await Scalar("SELECT count(*) FROM snapshot_records WHERE record_key LIKE '%-2'"));
        Assert.Equal(1, await Scalar(
            "SELECT count(*) FROM snapshot_records WHERE json_extract(json, '$.Company') = 'Fictional Labs'"));

        var changelog = result.GetProperty("changelog");
        Assert.Equal(5, changelog.GetProperty("new").GetInt32());
        Assert.Equal(NewestProcessedAt, changelog.GetProperty("checkpoint_processed_at").GetInt64());
        Assert.Equal(2, await Scalar(
            "SELECT count(*) FROM changelog_events WHERE activity_id = '00000000-0000-4000-8000-000000000003'"));
        Assert.Equal(1, await Scalar("SELECT count(*) FROM changelog_events WHERE method = 'DELETE'"));
    }

    [Fact]
    public async Task Repeated_syncs_are_idempotent()
    {
        StoreToken();
        await Member(Api_(), "sync");

        var (exitCode, json, _, _) = await Member(Api_(), "sync");
        Assert.Equal(0, exitCode);
        Assert.Equal("incremental", json.GetProperty("result").GetProperty("strategy").GetString());
        Assert.Equal(0, json.GetProperty("result").GetProperty("changelog").GetProperty("new").GetInt32());

        (exitCode, json, _, _) = await Member(Api_(), "sync", "--full");
        Assert.Equal(0, exitCode);
        var domains = Domains(json).Values.ToList();
        Assert.All(domains, d => Assert.Equal(0, d.GetProperty("added").GetInt32()));
        Assert.All(domains, d => Assert.Equal(0, d.GetProperty("removed").GetInt32()));
        Assert.Equal(8, await Scalar("SELECT count(*) FROM snapshot_records WHERE deleted_at IS NULL"));
        Assert.Equal(5, await Scalar("SELECT count(*) FROM changelog_events"));
    }

    [Fact]
    public async Task Complete_snapshot_tombstones_missing_records()
    {
        StoreToken();
        await Member(Api_(), "sync");
        var page = System.Text.Json.Nodes.JsonNode.Parse(Fixtures.Read("snapshot-all-0.json"))!;
        page["elements"]![0]!["snapshotData"]!.AsArray().RemoveAt(2);
        var fewer = page.ToJsonString();

        var (exitCode, json, _, _) = await Member(Api_(allPage: i => i == 0 ? fewer : null), "sync", "--full");

        Assert.Equal(0, exitCode);
        Assert.Equal(1, Domains(json)["CONNECTIONS"].GetProperty("removed").GetInt32());
        (_, json, _, _) = await Member(null, "data", "list", "--domain", "CONNECTIONS");
        Assert.Equal(2, json.GetProperty("result").GetArrayLength());
        (_, json, _, _) = await Member(null, "data", "list", "--domain", "CONNECTIONS", "--include-deleted");
        Assert.Equal(3, json.GetProperty("result").GetArrayLength());
    }

    [Fact]
    public async Task Failed_snapshot_pass_never_deletes()
    {
        StoreToken();
        await Member(Api_(), "sync");
        // Page 1 fails with a non-retried error after page 0 returned only part of the data.
        var failing = Api_(first: h => h.When(Api + "memberSnapshotData").WithExactQueryString("q=criteria&start=1")
            .Respond(HttpStatusCode.BadRequest, "application/json", """{"message":"Synthetic failure","status":400}"""));

        var (exitCode, json, _, _) = await Member(failing, "sync", "--full");

        Assert.Equal(5, exitCode);
        Assert.False(json.GetProperty("result").GetProperty("snapshot").GetProperty("complete").GetBoolean());
        Assert.Equal(8, await Scalar("SELECT count(*) FROM snapshot_records WHERE deleted_at IS NULL"));
    }

    [Fact]
    public async Task Domain_missing_from_a_later_snapshot_is_kept_and_reported()
    {
        StoreToken();
        await Member(Api_(), "sync");

        var (exitCode, json, _, stderr) = await Member(Api_(pages: 3), "sync", "--full");

        Assert.Equal(0, exitCode);
        Assert.Equal("not_returned", Domains(json)["WHATSAPP_NUMBERS"].GetProperty("status").GetString());
        Assert.Contains("WHATSAPP_NUMBERS", stderr);
        Assert.Equal(1, await Scalar(
            "SELECT count(*) FROM snapshot_records WHERE domain = 'WHATSAPP_NUMBERS' AND deleted_at IS NULL"));
    }

    [Fact]
    public async Task Single_domain_refresh_and_unsupported_domain()
    {
        StoreToken();
        await Member(Api_(), "sync");
        var http = Api_(first: h => h.When(Api + "memberSnapshotData")
            .WithExactQueryString("q=criteria&domain=Events&start=0")
            .Respond("application/json", Fixtures.Read("snapshot-all-2.json")));

        var (exitCode, json, _, _) = await Member(http, "sync", "--domain", "Events");
        Assert.Equal(0, exitCode);
        Assert.Equal("synced", Domains(json)["Events"].GetProperty("status").GetString());

        (exitCode, json, _, _) = await Member(Api_(), "sync", "--domain", "LOGIN");
        Assert.Equal(0, exitCode);
        Assert.Equal("unsupported", Domains(json)["LOGIN"].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Database_is_bound_to_one_member()
    {
        StoreToken();
        await Member(Api_(), "sync");

        var (exitCode, json, _, _) = await Member(Api_(member: "urn:li:person:SyntheticB2"), "sync");

        Assert.Equal(7, exitCode);
        Assert.Contains("SyntheticB2", json.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(0, await Scalar("SELECT count(*) FROM changelog_events WHERE owner = 'urn:li:person:SyntheticB2'"));
    }

    [Fact]
    public async Task Changelog_gap_beyond_retention_triggers_recovery_snapshot()
    {
        StoreToken();
        await Member(Api_(), "sync");
        _time.Advance(TimeSpan.FromDays(30));

        var (exitCode, json, _, stderr) = await Member(Api_(), "sync");

        Assert.Equal(0, exitCode);
        var result = json.GetProperty("result");
        Assert.Equal("recovery", result.GetProperty("strategy").GetString());
        Assert.Contains("permanently missing", result.GetProperty("changelog").GetProperty("gap").GetProperty("message").GetString());
        Assert.Contains("does not restore", stderr);

        (_, json, _, _) = await Member(null, "sync", "status");
        Assert.Equal(JsonValueKind.Null, json.GetProperty("result").GetProperty("changelog_gap").ValueKind);
    }

    [Fact]
    public async Task Approaching_retention_warns_without_snapshot()
    {
        StoreToken();
        await Member(Api_(), "sync");
        _time.Advance(TimeSpan.FromHours(23));
        await Member(Api_(), "sync");
        _time.Advance(TimeSpan.FromDays(22));

        var (_, json, _, _) = await Member(null, "sync", "status");

        Assert.Contains("sync within", json.GetProperty("result").GetProperty("changelog_gap").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Daily_refresh_reads_the_snapshot_again()
    {
        StoreToken();
        await Member(Api_(), "sync");
        _time.Advance(TimeSpan.FromDays(2));

        var (_, json, _, _) = await Member(Api_(), "sync");

        Assert.Equal("refresh", json.GetProperty("result").GetProperty("strategy").GetString());
    }

    // Reading

    [Fact]
    public async Task Data_get_and_database_info()
    {
        StoreToken();
        await Member(Api_(), "sync");
        var (_, json, _, _) = await Member(null, "data", "list", "--domain", "Events", "--limit", "1");
        var key = json.GetProperty("result")[0].GetProperty("record_key").GetString()!;

        (var exitCode, json, _, _) = await Member(null, "data", "get", "--domain", "Events", "--id", key);
        Assert.Equal(0, exitCode);
        Assert.Equal("Synthetic Meetup 1", json.GetProperty("result").GetProperty("data").GetProperty("Event Name").GetString());

        (exitCode, json, _, _) = await Member(null, "database", "info");
        Assert.Equal(0, exitCode);
        Assert.Equal(8, json.GetProperty("result").GetProperty("snapshot_records").GetInt64());
        Assert.Equal("urn:li:person:SyntheticA1", json.GetProperty("result").GetProperty("member").GetString());

        (exitCode, _, _, _) = await Member(null, "data", "list", "--limit", "5000");
        Assert.Equal(2, exitCode);
    }

    [Theory]
    [InlineData("DELETE FROM snapshot_records")]
    [InlineData("UPDATE snapshot_records SET json = '{}'")]
    [InlineData("INSERT INTO database_info VALUES ('x', 'y')")]
    [InlineData("DROP TABLE snapshot_records")]
    [InlineData("SELECT 1; DELETE FROM snapshot_records")]
    [InlineData("ATTACH DATABASE 'other.db' AS other")]
    [InlineData("DETACH DATABASE main")]
    [InlineData("PRAGMA query_only = OFF")]
    [InlineData("PRAGMA table_info(snapshot_records)")]
    [InlineData("SELECT load_extension('evil')")]
    [InlineData("CREATE VIRTUAL TABLE v USING fts5(a)")]
    [InlineData("VACUUM INTO 'copy.db'")]
    [InlineData("CREATE TEMP TABLE t AS SELECT * FROM snapshot_records")]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM snapshot_records")]
    public async Task Unsafe_sql_is_rejected(string sql)
    {
        StoreToken();
        await Member(Api_(), "sync");

        var (exitCode, json, _, stderr) = await Member(null, "data", "query", sql);

        Assert.True(exitCode == 8, stderr);
        Assert.Equal("unsafe_query", json.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(8, await Scalar("SELECT count(*) FROM snapshot_records WHERE deleted_at IS NULL"));
        Assert.False(File.Exists(Path.Combine(_dir, "copy.db")));
    }

    [Fact]
    public async Task Query_limits_rows_and_times_out()
    {
        StoreToken();
        await Member(Api_(), "sync");

        var (exitCode, json, _, stderr) = await Member(null, "data", "query",
            "SELECT domain, domain FROM snapshot_records -- comment", "--max-rows", "2");
        Assert.Equal(0, exitCode);
        var result = json.GetProperty("result");
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(["domain", "domain_2"], result.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        Assert.Contains("stopped after 2 rows", stderr);

        (exitCode, _, _, _) = await Member(null, "data", "query",
            "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT count(*) FROM c", "--timeout", "1");
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Query_from_file()
    {
        StoreToken();
        await Member(Api_(), "sync");
        var file = Path.Combine(_dir, "overview.sql");
        await File.WriteAllTextAsync(file, "SELECT domain, count(*) AS n FROM snapshot_records GROUP BY domain ORDER BY domain;\n");

        var (exitCode, json, _, _) = await Member(null, "data", "query", "--file", file);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, json.GetProperty("result").GetProperty("rows").GetArrayLength());
    }

    // Database

    [Fact]
    public async Task Purge_requires_confirmation()
    {
        StoreToken();
        await Member(Api_(), "sync");

        var (exitCode, _, _, _) = await Member(null, "database", "purge");
        Assert.Equal(2, exitCode);
        Assert.True(File.Exists(Database));

        (exitCode, _, _, _) = await Member(null, "database", "purge", "--yes");
        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(Database));
        Assert.NotNull(_keychain.Get("access-token"));
    }

    [Fact]
    public async Task Corrupt_database_is_reported()
    {
        await File.WriteAllTextAsync(Database, "this is not an SQLite database, just synthetic junk text padding......");

        var (exitCode, json, _, _) = await Member(null, "database", "info");

        Assert.Equal(7, exitCode);
        Assert.Equal("database", json.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Invalid_arguments_exit_with_usage()
    {
        var (exitCode, _, _, stderr) = await Member(null, "sync", "--no-such-option");

        Assert.Equal(2, exitCode);
        Assert.Contains("member --help", stderr);
    }

    // Skill

    [Fact]
    public async Task Skill_install_writes_both_targets_idempotently()
    {
        var (exitCode, json, _, _) = await Member(null, "skill", "install");

        Assert.Equal(0, exitCode);
        Assert.Equal(["created", "created"], json.GetProperty("result").EnumerateArray().Select(r => r.GetProperty("status").GetString()));
        var claude = Path.Combine(_dir, ".claude", "skills", "member", "SKILL.md");
        var agents = Path.Combine(_dir, ".agents", "skills", "member", "SKILL.md");
        Assert.StartsWith("---\nname: member\n", await File.ReadAllTextAsync(claude));
        Assert.Equal(await File.ReadAllTextAsync(claude), await File.ReadAllTextAsync(agents));

        (_, json, _, _) = await Member(null, "skill", "install");
        Assert.All(json.GetProperty("result").EnumerateArray(), r => Assert.Equal("unchanged", r.GetProperty("status").GetString()));

        await File.WriteAllTextAsync(claude, "---\nname: member\ndescription: old version\n---\n");
        (_, json, _, _) = await Member(null, "skill", "install", "--target", "claude");
        Assert.Equal("updated", json.GetProperty("result")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Skill_install_does_not_overwrite_a_foreign_skill_without_force()
    {
        var path = Path.Combine(_dir, "custom", "member", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "---\nname: someone-elses-skill\n---\n");

        var (exitCode, _, _, _) = await Member(null, "skill", "install", "--dir", Path.Combine(_dir, "custom"));
        Assert.Equal(2, exitCode);
        Assert.Contains("someone-elses-skill", await File.ReadAllTextAsync(path));

        (exitCode, _, _, _) = await Member(null, "skill", "install", "--dir", Path.Combine(_dir, "custom"), "--force");
        Assert.Equal(0, exitCode);
        Assert.Contains("name: member", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Skill_install_leaves_a_symlinked_skill_alone()
    {
        var checkout = Path.Combine(_dir, "checkout", "member");
        Directory.CreateDirectory(checkout);
        await File.WriteAllTextAsync(Path.Combine(checkout, "SKILL.md"), "---\nname: member\ndescription: working copy\n---\n");
        Directory.CreateDirectory(Path.Combine(_dir, ".claude", "skills"));
        Directory.CreateSymbolicLink(Path.Combine(_dir, ".claude", "skills", "member"), checkout);

        var (exitCode, json, _, _) = await Member(null, "skill", "install", "--target", "claude");

        Assert.Equal(0, exitCode);
        Assert.Equal("linked", json.GetProperty("result")[0].GetProperty("status").GetString());
        Assert.Contains("working copy", await File.ReadAllTextAsync(Path.Combine(checkout, "SKILL.md")));
    }

    [Fact]
    public async Task Skill_show_prints_the_embedded_skill()
    {
        var (exitCode, _, stdout, _) = await MemberRaw("skill", "show");

        Assert.Equal(0, exitCode);
        Assert.Equal(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SKILL.md")).ReplaceLineEndings("\n"), stdout);
    }

    private async Task<(int, string, string, string)> MemberRaw(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await Program.Run(args, stdout, stderr, new StringReader(""), new CliEnvironment { HomeDirectory = _dir });
        return (exitCode, "", stdout.ToString(), stderr.ToString());
    }
}
