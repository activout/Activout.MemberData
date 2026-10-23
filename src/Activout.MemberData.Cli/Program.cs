using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Activout.MemberData.Cli.Storage;

namespace Activout.MemberData.Cli;

/// <summary>Everything the command line touches outside its arguments, replaceable in tests.</summary>
public sealed record CliEnvironment
{
    public HttpMessageHandler? Http { get; init; }
    public string BaseUrl { get; init; } = MemberDataClient.DefaultBaseUrl;
    internal ICredentialStore? Keychain { get; init; }
    public Func<string, string?> GetEnv { get; init; } = Environment.GetEnvironmentVariable;
    public bool OutputRedirected { get; init; } = Console.IsOutputRedirected;
    public bool InputRedirected { get; init; } = Console.IsInputRedirected;
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public string HomeDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

public sealed record SkillInstall(string Path, string Status);

public static class Program
{
    public static Task<int> Main(string[] args) =>
        Run(args, Console.Out, Console.Error, Console.In, new CliEnvironment());

    /// <summary>Entry point for tests: explicit streams and environment.</summary>
    public static async Task<int> Run(string[] args, TextWriter stdout, TextWriter stderr, TextReader stdin,
        CliEnvironment env, CancellationToken ct = default)
    {
        var app = new App(stdout, stderr, stdin, env);
        // Bare `member` shows the help instead of a "Required command" error.
        var parseResult = app.Root.Parse(args.Length == 0 ? ["--help"] : args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors) stderr.WriteLine($"member: {error.Message}");
            stderr.WriteLine("Run 'member --help' for usage.");
            return ExitCodes.Usage;
        }

        return await parseResult.InvokeAsync(
            new InvocationConfiguration { Output = stdout, Error = stderr, EnableDefaultExceptionHandler = false }, ct);
    }
}

internal sealed class App
{
    private const int MaxListLimit = 1000;

    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly TextReader _stdin;
    private readonly CliEnvironment _env;

    private readonly Option<string?> _database = new("--database")
    {
        Description = "SQLite database path (default: member.db in the platform's application data directory)",
        Recursive = true,
    };

    private readonly Option<string?> _format = new("--format")
    {
        Description = "Output format: json (default when stdout is redirected) or table",
        Recursive = true,
    };

    private readonly Option<string?> _credentialSource = new("--credential-source")
    {
        Description = $"keychain (default) or environment ({CredentialSources.TokenVariable}, explicit opt-in at " +
                      $"your own risk). Also settable with {CredentialSources.SourceVariable}",
        Recursive = true,
    };

    public RootCommand Root { get; }

    public App(TextWriter stdout, TextWriter stderr, TextReader stdin, CliEnvironment env)
    {
        _stdout = stdout;
        _stderr = stderr;
        _stdin = stdin;
        _env = env;
        _format.AcceptOnlyFromAmong("json", "table");
        _credentialSource.AcceptOnlyFromAmong(CredentialSources.Keychain, CredentialSources.Environment);

        Root = new RootCommand("""
            Sync your own LinkedIn Member Data Portability (DMA) data into a local SQLite database and query it.
            Unofficial; not affiliated with or endorsed by LinkedIn. Reads only; never changes your LinkedIn account.
            """)
        {
            _database, _format, _credentialSource,
            AuthCommand(), DomainsCommand(), SyncCommand(), DataCommand(), DatabaseCommand(), SkillCommand(),
        };
    }

    // auth

    private Command AuthCommand()
    {
        var status = new Command("status", "Show the credential source and safe token metadata (offline). Never prints the token.");
        status.SetAction((p, _) => Execute(p, output =>
        {
            var (source, store) = Store(p);
            bool available;
            StoredToken? token = null;
            string? problem = null;
            try
            {
                token = StoredToken.Parse(store.Get(StoredToken.Key));
                available = true;
            }
            catch (CredentialStoreUnavailableException e)
            {
                available = false;
                problem = e.Message;
            }

            var expired = token?.ExpiresAt is { } at && at <= _env.Time.GetUtcNow();
            var action = !available ? problem
                : token == null ? (source == CredentialSources.Environment
                    ? $"Set {CredentialSources.TokenVariable}"
                    : "Run 'member auth import-token'")
                : expired ? "The token has expired: generate a new one and run 'member auth import-token'"
                : null;
            var result = new
            {
                credential_source = source, store = store.Name, available, token_present = token != null,
                imported_at = token?.ImportedAt, expires_at = token?.ExpiresAt, expired, action_required = action,
            };
            output.Result(result, () =>
            {
                output.Out.WriteLine($"Credential source: {source} ({store.Name})");
                output.Out.WriteLine($"Available:         {(available ? "yes" : "no")}");
                output.Out.WriteLine($"Token present:     {(token != null ? "yes" : "no")}");
                output.Out.WriteLine($"Imported at:       {Format(token?.ImportedAt)}");
                output.Out.WriteLine($"Expires at:        {Format(token?.ExpiresAt)}{(expired ? " (expired)" : "")}");
                if (action != null) output.Out.WriteLine($"Action required:   {action}");
            }, available && token != null && !expired ? ExitCodes.Success : ExitCodes.AuthRequired);
            return Task.FromResult(available && token != null && !expired ? ExitCodes.Success : ExitCodes.AuthRequired);
        }));

        var stdinOption = new Option<bool>("--stdin") { Description = "Read the token from standard input instead of a hidden prompt" };
        var expiresAt = new Option<DateTimeOffset?>("--expires-at") { Description = "Token expiry as shown by LinkedIn's token generator (ISO 8601)" };
        var expiresInDays = new Option<int?>("--expires-in-days") { Description = "Token lifetime in days from now, as shown by LinkedIn's token generator" };
        var noVerify = new Option<bool>("--no-verify") { Description = "Store without checking the token against LinkedIn" };
        var import = new Command("import-token", """
            Store an access token from LinkedIn's OAuth Token Generator in the operating system's secure store.
            The token is read from a hidden prompt or stdin, never from arguments, and is checked against LinkedIn.
            Examples:
              member auth import-token
              pbpaste | member auth import-token --stdin --expires-in-days 60
            """) { stdinOption, expiresAt, expiresInDays, noVerify };
        import.SetAction((p, ct) => Execute(p, async output =>
        {
            var (source, store) = Store(p);
            if (source == CredentialSources.Environment)
                throw new MemberUsageException($"The environment source is read-only; set {CredentialSources.TokenVariable} instead");

            var token = p.GetValue(stdinOption) || _env.InputRedirected
                ? (await _stdin.ReadToEndAsync(ct)).Trim()
                : ReadHidden("LinkedIn access token: ");
            if (string.IsNullOrEmpty(token)) throw new MemberUsageException("No token was provided");
            if (token.Any(char.IsWhiteSpace)) throw new MemberUsageException("The token must be a single line without spaces");

            var expires = p.GetValue(expiresAt) ?? (p.GetValue(expiresInDays) is { } days
                ? _env.Time.GetUtcNow().AddDays(days)
                : null);
            MemberAuthorization? authorization = null;
            if (!p.GetValue(noVerify))
            {
                authorization = await Client(token).GetAuthorization(ct);
            }

            store.Set(StoredToken.Key, new StoredToken(token, _env.Time.GetUtcNow(), expires).Serialize());
            var result = new
            {
                stored_in = store.Name, verified = authorization != null, member = authorization?.Member,
                developer_application = authorization?.DeveloperApplication, expires_at = expires,
            };
            output.Result(result, () =>
            {
                output.Out.WriteLine($"Token stored in {store.Name}.");
                if (authorization != null) output.Out.WriteLine($"Verified for {authorization.Member}.");
                else if (!p.GetValue(noVerify)) output.Out.WriteLine("Warning: LinkedIn reports no DMA registration yet; see 'member auth registration'.");
            });
            return ExitCodes.Success;
        }));

        var logout = new Command("logout", "Remove the stored token from the secure store. The database is kept.");
        logout.SetAction((p, _) => Execute(p, output =>
        {
            var (_, store) = Store(p);
            var removed = store.Delete(StoredToken.Key);
            output.Result(new { removed, store = store.Name },
                () => output.Out.WriteLine(removed ? $"Token removed from {store.Name}." : "No stored token."));
            return Task.FromResult(ExitCodes.Success);
        }));

        var registration = new Command("registration", """
            Check with LinkedIn whether the member is registered for DMA data generation (online, read-only).
            Use when no data appears a few minutes after generating the token.
            """);
        registration.SetAction((p, ct) => Execute(p, async output =>
        {
            var authorization = await Client(p).GetAuthorization(ct);
            var result = new
            {
                registered = authorization != null, member = authorization?.Member,
                developer_application = authorization?.DeveloperApplication, regulated_at = authorization?.RegulatedAt,
                scopes = authorization?.Scopes ?? [],
            };
            output.Result(result, () =>
            {
                if (authorization == null)
                {
                    output.Out.WriteLine("Not registered for DMA data generation. Try 'member auth register'.");
                    return;
                }

                output.Out.WriteLine($"Registered:  {authorization.Member}");
                output.Out.WriteLine($"Application: {authorization.DeveloperApplication}");
                output.Out.WriteLine($"Since:       {Format(authorization.RegulatedAt)}");
                output.Out.WriteLine($"Scopes:      {string.Join(", ", authorization.Scopes)}");
            });
            return ExitCodes.Success;
        }));

        var register = new Command("register", """
            Ask LinkedIn to start DMA data generation for the member (online). LinkedIn normally does this
            automatically when the token is generated; use only when 'member auth registration' reports none.
            """);
        register.SetAction((p, ct) => Execute(p, async output =>
        {
            var client = Client(p);
            await client.Register(ct);
            var authorization = await client.GetAuthorization(ct);
            output.Result(new { registered = authorization != null, member = authorization?.Member, regulated_at = authorization?.RegulatedAt },
                () => output.Out.WriteLine(authorization != null
                    ? $"Registered {authorization.Member} since {Format(authorization.RegulatedAt)}."
                    : "LinkedIn accepted the request but reports no registration yet; check again in a few minutes."));
            return ExitCodes.Success;
        }));

        return new Command("auth", "Access token management.") { status, import, logout, registration, register };
    }

    // domains

    private Command DomainsCommand()
    {
        var list = new Command("list", "Documented and discovered snapshot domains with their local status (offline).");
        list.SetAction((p, _) => Execute(p, async output =>
        {
            using var database = await OpenDatabase(p);
            var rows = (await database.Dao.ListDomains()).ToDictionary(r => r.Domain);
            var counts = (await database.Dao.CountRecords()).ToDictionary(r => r.Domain);
            var names = SnapshotDomains.Documented.Concat(rows.Keys).Concat(counts.Keys).Distinct()
                .Order(StringComparer.Ordinal);
            var result = names.Select(n =>
            {
                rows.TryGetValue(n, out var r);
                counts.TryGetValue(n, out var c);
                return new
                {
                    domain = n, documented = SnapshotDomains.Documented.Contains(n), status = r?.Status ?? "never_synced",
                    records = c?.Live ?? 0, deleted_records = c?.Deleted ?? 0, last_attempt_at = r?.LastAttemptAt,
                    last_success_at = r?.LastSuccessAt, last_error = r?.LastError,
                };
            }).ToList();
            output.Result(result, () => output.Table(["DOMAIN", "DOCUMENTED", "STATUS", "RECORDS", "LAST SUCCESS"],
                result.Select(d => new[]
                {
                    d.domain, d.documented ? "yes" : "no", d.status, d.records.ToString(CultureInfo.InvariantCulture),
                    d.last_success_at ?? "-",
                })));
            return ExitCodes.Success;
        }));
        return new Command("domains", "Snapshot domains.") { list };
    }

    // sync

    private Command SyncCommand()
    {
        var full = new Option<bool>("--full") { Description = "Refresh every snapshot domain, then catch up the changelog" };
        var domain = new Option<string?>("--domain") { Description = "Refresh only this snapshot domain (case sensitive)" };
        var command = new Command("sync", """
            Fetch the changelog and, when needed, the snapshot from LinkedIn into the database (online).
            Without options the strategy is chosen safely: an initial full snapshot, a daily snapshot refresh, a
            recovery snapshot after a changelog gap, otherwise only the changelog.
            Examples:
              member sync
              member sync --full
              member sync --domain CONNECTIONS
            """) { full, domain };
        command.SetAction((p, ct) => Execute(p, async output =>
        {
            var client = Client(p);
            using var database = await OpenDatabase(p);
            using var syncLock = AcquireLock(database.Path);
            var result = await new Synchroniser(client, database, _env.Time)
                .Run(new SyncOptions(p.GetValue(full), p.GetValue(domain)), output.Progress, ct);
            var exitCode = result.Ok ? ExitCodes.Success : ExitCodes.PartialSync;
            foreach (var w in result.Warnings) output.Progress("warning: " + w);
            output.Result(result, () => SyncSummary(output, result), exitCode);
            return exitCode;
        }));

        var status = new Command("status", "Checkpoints, recent runs, changelog age and retention warnings (offline).");
        status.SetAction((p, _) => Execute(p, async output =>
        {
            using var database = await OpenDatabase(p);
            var dao = database.Dao;
            var lastChangelog = MemberDatabase.ParseTimestamp(await dao.GetCheckpoint(Synchroniser.ChangelogLastSuccess));
            var gap = new Synchroniser(Client("unused"), database, _env.Time).DetectGap(lastChangelog, null);
            var result = new
            {
                member = await dao.GetInfo(MemberDatabase.MemberKey),
                last_full_snapshot_at = await dao.GetCheckpoint(Synchroniser.SnapshotLastFullSuccess),
                changelog_last_success_at = lastChangelog,
                changelog_processed_at = await dao.GetCheckpoint(Synchroniser.ChangelogProcessedAt),
                changelog_events = await dao.CountEvents(),
                changelog_gap = gap,
                recent_runs = (await dao.RecentRuns(10)).Select(r => new
                {
                    r.Id, r.Kind, r.Mode, r.Domain, r.StartedAt, r.FinishedAt, r.Status,
                }),
            };
            output.Result(result, () =>
            {
                output.Out.WriteLine($"Member:              {result.member ?? "-"}");
                output.Out.WriteLine($"Last full snapshot:  {result.last_full_snapshot_at ?? "never"}");
                output.Out.WriteLine($"Changelog synced:    {Format(lastChangelog)}");
                output.Out.WriteLine($"Changelog events:    {result.changelog_events}");
                if (gap != null) output.Out.WriteLine($"Warning: {gap.Message}");
            });
            return ExitCodes.Success;
        }));
        command.Add(status);
        return command;
    }

    private static void SyncSummary(Output output, SyncResult result)
    {
        output.Out.WriteLine($"Member {result.Member}, strategy {result.Strategy}.");
        if (result.Changelog is { } c)
            output.Out.WriteLine($"Changelog: {c.Status}, {c.New} new of {c.Received} received" +
                                 (c.Error != null ? $" ({c.Error})" : ""));
        if (result.Snapshot is { } s)
        {
            output.Out.WriteLine($"Snapshot: {s.Status}" + (s.Error != null ? $" ({s.Error})" : ""));
            output.Table(["DOMAIN", "STATUS", "PAGES", "RECORDS", "ADDED", "REMOVED"],
                s.Domains.Where(d => d.Status != "no_data").Select(d => new[]
                {
                    d.Domain, d.Status, d.Pages.ToString(CultureInfo.InvariantCulture),
                    d.Records.ToString(CultureInfo.InvariantCulture), d.Added.ToString(CultureInfo.InvariantCulture),
                    d.Removed.ToString(CultureInfo.InvariantCulture),
                }));
        }
    }

    // data

    private Command DataCommand()
    {
        var domain = new Option<string?>("--domain") { Description = "Snapshot domain, e.g. CONNECTIONS" };
        var limit = new Option<int>("--limit") { Description = $"Maximum records (1-{MaxListLimit})", DefaultValueFactory = _ => 50 };
        var offset = new Option<int>("--offset") { Description = "Records to skip", DefaultValueFactory = _ => 0 };
        var includeDeleted = new Option<bool>("--include-deleted") { Description = "Include records no longer in LinkedIn's snapshot" };
        var list = new Command("list", """
            List stored snapshot records (offline), ordered by domain, first seen and key.
            Example: member data list --domain CONNECTIONS --limit 50 --offset 50
            """) { domain, limit, offset, includeDeleted };
        list.SetAction((p, _) => Execute(p, async output =>
        {
            var max = p.GetValue(limit);
            if (max is < 1 or > MaxListLimit) throw new MemberUsageException($"--limit must be between 1 and {MaxListLimit}");
            if (p.GetValue(offset) < 0) throw new MemberUsageException("--offset must not be negative");
            using var database = await OpenDatabase(p);
            var rows = (await database.Dao.ListRecords(p.GetValue(domain), p.GetValue(includeDeleted), max,
                p.GetValue(offset))).ToList();
            var records = rows.Select(Record).ToList();
            output.Result(records, () => output.Table(["DOMAIN", "RECORD KEY", "DATA"],
                rows.Select(r => new[] { r.Domain, r.RecordKey, Truncate(r.Json, 100) })));
            return ExitCodes.Success;
        }));

        var getDomain = new Option<string>("--domain") { Description = "Snapshot domain", Required = true };
        var id = new Option<string>("--id") { Description = "Record key from 'member data list'", Required = true };
        var get = new Command("get", "Show one stored snapshot record (offline).") { getDomain, id };
        get.SetAction((p, _) => Execute(p, async output =>
        {
            using var database = await OpenDatabase(p);
            var row = await database.Dao.GetRecord(p.GetValue(getDomain)!, p.GetValue(id)!) ??
                      throw new MemberUsageException("No such record");
            var record = Record(row);
            output.Result(record, () => output.Out.WriteLine(JsonSerializer.Serialize(record, Output.JsonOptions)));
            return ExitCodes.Success;
        }));

        var sql = new Argument<string?>("sql") { Description = "One read-only SELECT statement", Arity = ArgumentArity.ZeroOrOne };
        var file = new Option<string?>("--file") { Description = "Read the SELECT statement from this file" };
        var maxRows = new Option<int>("--max-rows") { Description = "Stop after this many rows (result marked truncated)", DefaultValueFactory = _ => 1000 };
        var timeout = new Option<int>("--timeout") { Description = "Seconds before the query is interrupted", DefaultValueFactory = _ => 30 };
        var query = new Command("query", """
            Run one read-only SQL SELECT against the local database (offline). Tables: snapshot_records,
            changelog_events, snapshot_domains, sync_runs, sync_checkpoints, database_info. Use SQLite JSON
            functions on the json column. The database holds personal data about you and other people:
            never expose this command to untrusted clients.
            Examples:
              member data query "SELECT domain, count(*) AS n FROM snapshot_records GROUP BY domain"
              member data query "SELECT json_extract(json, '$.Company') AS company, count(*) AS n FROM snapshot_records WHERE domain = 'CONNECTIONS' AND deleted_at IS NULL GROUP BY 1 ORDER BY n DESC"
              member data query --file ./queries/overview.sql
            """) { sql, file, maxRows, timeout };
        query.SetAction((p, ct) => Execute(p, async output =>
        {
            var text = p.GetValue(file) is { } path
                ? await File.ReadAllTextAsync(path, ct)
                : p.GetValue(sql) ?? throw new MemberUsageException("Give a SELECT statement or --file");
            if (p.GetValue(file) != null && p.GetValue(sql) != null)
                throw new MemberUsageException("Give either a SELECT statement or --file, not both");
            var rows = p.GetValue(maxRows);
            if (rows is < 1 or > 1_000_000) throw new MemberUsageException("--max-rows must be between 1 and 1000000");
            var result = await SafeQuery.Run(DatabasePath(p), text, rows, TimeSpan.FromSeconds(p.GetValue(timeout)), ct);
            if (result.Truncated) output.Progress($"warning: stopped after {rows} rows; use --max-rows or LIMIT/OFFSET");
            output.Result(result, () => output.Table(result.Columns,
                result.Rows.Select(r => result.Columns.Select(c => Truncate(Convert.ToString(r[c], CultureInfo.InvariantCulture), 80)).ToList())));
            return ExitCodes.Success;
        }));

        return new Command("data", "Read the local database.") { list, get, query };
    }

    // database

    private Command DatabaseCommand()
    {
        var info = new Command("info", "Path, schema version, member binding and record counts (offline).");
        info.SetAction((p, _) => Execute(p, async output =>
        {
            using var database = await OpenDatabase(p);
            var dao = database.Dao;
            var counts = (await dao.CountRecords()).ToList();
            var result = new
            {
                path = database.Path, schema_version = await dao.GetSchemaVersion(),
                size_bytes = new FileInfo(database.Path).Length,
                created_at = await dao.GetInfo("created_at"),
                member = await dao.GetInfo(MemberDatabase.MemberKey),
                developer_application = await dao.GetInfo(MemberDatabase.ApplicationKey),
                snapshot_records = counts.Sum(c => c.Live), deleted_snapshot_records = counts.Sum(c => c.Deleted),
                domains_with_records = counts.Count(c => c.Live > 0),
                changelog_events = await dao.CountEvents(),
            };
            output.Result(result, () =>
            {
                output.Out.WriteLine($"Path:             {result.path}");
                output.Out.WriteLine($"Schema version:   {result.schema_version}");
                output.Out.WriteLine($"Member:           {result.member ?? "- (not bound until the first sync)"}");
                output.Out.WriteLine($"Snapshot records: {result.snapshot_records} in {result.domains_with_records} domains ({result.deleted_snapshot_records} deleted)");
                output.Out.WriteLine($"Changelog events: {result.changelog_events}");
            });
            return ExitCodes.Success;
        }));

        var yes = new Option<bool>("--yes") { Description = "Confirm deleting the local database" };
        var purge = new Command("purge", """
            Delete the local database and its WAL files. Requires --yes. LinkedIn data and the token are untouched.
            """) { yes };
        purge.SetAction((p, _) => Execute(p, output =>
        {
            var path = DatabasePath(p);
            if (!p.GetValue(yes)) throw new MemberUsageException($"This deletes {path}; repeat with --yes to confirm");
            using (AcquireLock(path))
            {
                var deleted = new[] { path, path + "-wal", path + "-shm" }.Where(File.Exists).ToList();
                foreach (var f in deleted) File.Delete(f);
                output.Result(new { path, deleted = deleted.Count > 0 },
                    () => output.Out.WriteLine(deleted.Count > 0 ? $"Deleted {path}." : $"No database at {path}."));
            }

            return Task.FromResult(ExitCodes.Success);
        }));

        return new Command("database", "The local database.") { info, purge };
    }

    // skill

    private Command SkillCommand()
    {
        var target = new Option<string>("--target")
        {
            Description = "claude (~/.claude/skills: Claude Code), agents (~/.agents/skills: Codex, Gemini CLI, " +
                          "GitHub Copilot, OpenCode) or all",
            DefaultValueFactory = _ => "all",
        };
        target.AcceptOnlyFromAmong("all", "claude", "agents");
        var dir = new Option<string?>("--dir") { Description = "Install into this skills directory instead (a member/ folder is created in it)" };
        var force = new Option<bool>("--force") { Description = "Overwrite a member/SKILL.md that was not written by member" };
        var install = new Command("install", """
            Install the agent skill that teaches coding agents to use member (offline first, JSON, ask before
            syncing). Writes member/SKILL.md into each target's user-level skills directory; running it again
            updates the skill to this version.
            Examples:
              member skill install
              member skill install --target claude
              member skill install --dir ./.agents/skills
            """) { target, dir, force };
        install.SetAction((p, ct) => Execute(p, async output =>
        {
            var directories = p.GetValue(dir) is { } d
                ? [Path.GetFullPath(d)]
                : p.GetValue(target) switch
                {
                    "claude" => [Path.Combine(_env.HomeDirectory, ".claude", "skills")],
                    "agents" => [Path.Combine(_env.HomeDirectory, ".agents", "skills")],
                    _ => new[] { Path.Combine(_env.HomeDirectory, ".claude", "skills"), Path.Combine(_env.HomeDirectory, ".agents", "skills") },
                };
            var skill = Skill();
            var results = new List<SkillInstall>();
            foreach (var directory in directories)
            {
                var path = Path.Combine(directory, "member", "SKILL.md");
                string status;
                if (new DirectoryInfo(Path.GetDirectoryName(path)!).LinkTarget != null)
                {
                    // A developer's symlink to a checkout: writing through it would overwrite the repository's file.
                    status = "linked";
                }
                else if (!File.Exists(path))
                {
                    status = "created";
                }
                else
                {
                    var existing = await File.ReadAllTextAsync(path, ct);
                    if (existing == skill) status = "unchanged";
                    else if (existing.Contains("\nname: member\n") || p.GetValue(force)) status = "updated";
                    else throw new MemberUsageException($"{path} exists and was not written by member; use --force to replace it");
                }

                if (status is "created" or "updated")
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, skill, ct);
                }

                results.Add(new SkillInstall(path, status));
            }

            output.Result(results, () => output.Table(["SKILL", "STATUS"],
                results.Select(r => new[] { r.Path, r.Status })));
            return ExitCodes.Success;
        }));

        var show = new Command("show", "Print the agent skill (SKILL.md) for agents with other skill locations.");
        show.SetAction((_, _) =>
        {
            _stdout.Write(Skill());
            return Task.FromResult(ExitCodes.Success);
        });

        return new Command("skill", "The agent skill for Claude Code, Codex and other coding agents.") { install, show };
    }

    internal static string Skill()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("SKILL.md")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    // Helpers

    private Task<int> Execute(ParseResult p, Func<Output, Task<int>> action) =>
        Execute(new Output(_stdout, _stderr, (p.GetValue(_format) ?? (_env.OutputRedirected ? "json" : "table")) == "json"),
            action);

    private static async Task<int> Execute(Output output, Func<Output, Task<int>> action)
    {
        try
        {
            return await action(output);
        }
        catch (OperationCanceledException)
        {
            output.Progress("Cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (MemberDataApiException e) when (e.IsAuthentication)
        {
            var wrapped = new MemberAuthException(
                $"LinkedIn rejected the token ({e.ApiMessage}): it is invalid, expired or revoked. Generate a new one " +
                "with LinkedIn's OAuth Token Generator and run 'member auth import-token'.");
            output.Error(wrapped, ExitCodes.AuthRequired);
            return ExitCodes.AuthRequired;
        }
        catch (MemberDataApiException e) when (e.IsPermission)
        {
            var wrapped = new MemberAuthException(
                $"LinkedIn refused access ({e.ApiMessage}). The token needs the r_dma_portability_self_serve scope " +
                "from an app with the Member Data Portability (Member) product.");
            output.Error(wrapped, ExitCodes.AuthRequired);
            return ExitCodes.AuthRequired;
        }
        catch (Exception e)
        {
            var exitCode = ExitCodes.For(e);
            output.Error(e, exitCode);
            return exitCode;
        }
    }

    private string DatabasePath(ParseResult p) => MemberPaths.DatabasePath(p.GetValue(_database));

    private Task<MemberDatabase> OpenDatabase(ParseResult p) => MemberDatabase.Open(DatabasePath(p));

    private (string Source, ICredentialStore Store) Store(ParseResult p)
    {
        var source = p.GetValue(_credentialSource) ?? _env.GetEnv(CredentialSources.SourceVariable) ??
                     CredentialSources.Keychain;
        return source switch
        {
            CredentialSources.Keychain => (source, _env.Keychain ?? new KeychainCredentialStore()),
            CredentialSources.Environment => (source, new EnvironmentCredentialStore(_env.GetEnv)),
            _ => throw new MemberUsageException(
                $"{CredentialSources.SourceVariable} must be '{CredentialSources.Keychain}' or '{CredentialSources.Environment}'"),
        };
    }

    private MemberDataClient Client(ParseResult p)
    {
        var (source, store) = Store(p);
        var token = StoredToken.Parse(store.Get(StoredToken.Key)) ?? throw new MemberAuthException(
            source == CredentialSources.Environment
                ? $"{CredentialSources.TokenVariable} is not set"
                : "No access token; run 'member auth import-token'");
        if (token.ExpiresAt is { } at && at <= _env.Time.GetUtcNow())
            throw new MemberAuthException(
                $"The access token expired at {Format(at)}; generate a new one and run 'member auth import-token'");
        return Client(token.AccessToken);
    }

    private MemberDataClient Client(string token) => MemberDataClient.Create(token, _env.Http, _env.BaseUrl);

    private static FileStream AcquireLock(string databasePath)
    {
        MemberPaths.EnsurePrivateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            return new FileStream(databasePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            throw new SyncLockedException("Another member process is using this database; try again when it finishes");
        }
    }

    private string ReadHidden(string prompt)
    {
        _stderr.Write(prompt);
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0) text.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }

        _stderr.WriteLine();
        return text.ToString().Trim();
    }

    private static object Record(SnapshotRecordRow r) => new
    {
        domain = r.Domain, record_key = r.RecordKey, data = JsonSerializer.Deserialize<JsonElement>(r.Json),
        first_seen_at = r.FirstSeenAt, last_seen_at = r.LastSeenAt, deleted_at = r.DeletedAt,
    };

    private static string Truncate(string? value, int max)
    {
        var flat = (value ?? "").ReplaceLineEndings(" ");
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    private static string Format(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "-";
}
