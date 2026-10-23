using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Activout.MemberData.Cli.Storage;

namespace Activout.MemberData.Cli;

public sealed record DomainResult(string Domain, string Status, int Pages, int Records, int Added, int Removed,
    string? Error);

public sealed record SnapshotResult(string Mode, string Status, bool Complete, IReadOnlyList<DomainResult> Domains,
    string? Error);

public sealed record ChangelogGap(string Since, double Days, string Message);

public sealed record ChangelogResult(string Status, int Requests, int Received, int New, long? CheckpointProcessedAt,
    ChangelogGap? Gap, string? Error);

public sealed record SyncResult(string Member, string Strategy, ChangelogResult? Changelog, SnapshotResult? Snapshot,
    IReadOnlyList<string> Warnings)
{
    public bool Ok => Changelog?.Status != "failed" && Snapshot?.Status is null or "success";
}

public sealed record SyncOptions(bool Full = false, string? Domain = null, bool SkipChangelog = false);

/// <summary>
/// Synchronises one member's changelog and snapshot into the database. Snapshot records are stored as raw JSON with
/// a content-derived key, because LinkedIn's snapshot records have no identifiers; changelog events are an
/// append-only archive keyed by LinkedIn's event id. See docs/storage-and-sync.md.
/// </summary>
internal sealed class Synchroniser(MemberDataClient client, MemberDatabase database, TimeProvider? timeProvider = null)
{
    public const string ChangelogProcessedAt = "changelog.processed_at";
    public const string ChangelogLastSuccess = "changelog.last_success_at";
    public const string SnapshotLastFullSuccess = "snapshot.last_full_success_at";

    /// <summary>LinkedIn keeps changelog events for 28 days.</summary>
    public static readonly TimeSpan ChangelogRetention = TimeSpan.FromDays(28);

    /// <summary>The default strategy refreshes the snapshot when the last full one is older than this.</summary>
    public static readonly TimeSpan SnapshotRefreshInterval = TimeSpan.FromDays(1);

    /// <summary>Warn this long before the retention boundary.</summary>
    public static readonly TimeSpan ChangelogWarning = TimeSpan.FromDays(21);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private string Now => MemberDatabase.Timestamp(_time.GetUtcNow());

    public async Task<SyncResult> Run(SyncOptions options, Action<string> progress, CancellationToken ct)
    {
        var authorization = await client.GetAuthorization(ct) ?? throw new MemberAuthException(
            "LinkedIn reports no DMA registration for this token. Wait a few minutes after generating it, " +
            "check with 'member auth registration', and if needed run 'member auth register'.");
        await database.BindMember(authorization.Member, authorization.DeveloperApplication);

        var warnings = new List<string>();
        var lastFull = await database.Dao.GetCheckpoint(SnapshotLastFullSuccess);

        ChangelogResult? changelog = null;
        if (!options.SkipChangelog)
        {
            progress("Changelog: fetching events…");
            changelog = await SyncChangelog(authorization.RegulatedAt, ct);
            if (changelog.Gap != null) warnings.Add(changelog.Gap.Message);
        }

        string strategy;
        SnapshotResult? snapshot = null;
        if (options.Domain != null)
        {
            strategy = "domain";
        }
        else if (options.Full)
        {
            strategy = "full";
        }
        else if (lastFull == null)
        {
            strategy = "initial";
        }
        else if (changelog?.Gap is { Days: var days } && days >= ChangelogRetention.TotalDays)
        {
            strategy = "recovery";
            warnings.Add("A new snapshot reconciles current state; it does not restore changelog events that expired.");
        }
        else if (_time.GetUtcNow() - MemberDatabase.ParseTimestamp(lastFull) >= SnapshotRefreshInterval)
        {
            strategy = "refresh";
        }
        else
        {
            strategy = "incremental";
        }

        if (strategy != "incremental")
        {
            snapshot = await SyncSnapshot(options.Domain, strategy, progress, ct);
        }

        foreach (var d in snapshot?.Domains ?? [])
        {
            if (d.Status == "not_returned")
                warnings.Add($"{d.Domain} was not returned by LinkedIn; its {d.Records} stored records were kept.");
        }

        return new SyncResult(authorization.Member, strategy, changelog, snapshot, warnings);
    }

    // Changelog

    internal async Task<ChangelogResult> SyncChangelog(DateTimeOffset? regulatedAt, CancellationToken ct)
    {
        var dao = database.Dao;
        var runId = await dao.InsertRun("changelog", "incremental", null, Now);
        var checkpointText = await dao.GetCheckpoint(ChangelogProcessedAt);
        long? checkpoint = long.TryParse(checkpointText, CultureInfo.InvariantCulture, out var c) ? c : null;
        var gap = DetectGap(MemberDatabase.ParseTimestamp(await dao.GetCheckpoint(ChangelogLastSuccess)), regulatedAt);

        var startTime = checkpoint ?? regulatedAt?.ToUnixTimeMilliseconds();
        int requests = 0, received = 0, inserted = 0, offset = 0;
        string? error = null;
        try
        {
            while (true)
            {
                const int count = MemberDataClient.DefaultChangelogCount;
                var page = await client.GetChangelog(startTime, count, ct, offset);
                requests++;
                received += page.Events.Count;
                if (page.Events.Count == 0) break;

                var newOnPage = 0;
                var maxProcessed = startTime;
                await database.InTransaction(async t =>
                {
                    foreach (var e in page.Events)
                    {
                        var row = EventRow(e, runId);
                        newOnPage += await dao.InsertEvent(row, t);
                        if (row.ProcessedAt is { } p && (maxProcessed == null || p > maxProcessed)) maxProcessed = p;
                    }

                    // The checkpoint moves in the same transaction as the events it covers.
                    if (maxProcessed is { } m && (checkpoint == null || m > checkpoint))
                    {
                        checkpoint = m;
                        await dao.SetCheckpoint(ChangelogProcessedAt, m.ToString(CultureInfo.InvariantCulture), Now, t);
                    }
                });
                inserted += newOnPage;

                if (page.Events.Count < count) break;
                if (maxProcessed > startTime)
                {
                    // Inclusive startTime: the next page repeats the newest events, which deduplicate by id.
                    startTime = maxProcessed;
                    offset = 0;
                }
                else if (newOnPage > 0 || offset == 0)
                {
                    // A full page sharing one processedAt: page past it with start.
                    offset += count;
                }
                else
                {
                    error = "Changelog paging made no progress (a full page of already stored events with the same " +
                            "processedAt); stopped to avoid a loop. Run sync again later.";
                    break;
                }

                if (requests > 10_000) throw new InvalidOperationException("Changelog paging exceeded 10000 requests");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException && !IsAuthFailure(e))
        {
            error = Sanitise(e);
        }
        catch (Exception e) when (IsAuthFailure(e))
        {
            await dao.FinishRun(runId, Now, "failed", Details(new { error = Sanitise(e) }));
            throw;
        }

        var status = error == null ? "success" : inserted > 0 ? "partial" : "failed";
        if (error == null) await dao.SetCheckpoint(ChangelogLastSuccess, Now, Now);
        var result = new ChangelogResult(status, requests, received, inserted, checkpoint, gap, error);
        await dao.FinishRun(runId, Now, status, Details(result));
        return result;
    }

    internal ChangelogGap? DetectGap(DateTimeOffset? lastSuccess, DateTimeOffset? regulatedAt)
    {
        var since = lastSuccess ?? regulatedAt;
        if (since == null) return null;
        var age = _time.GetUtcNow() - since.Value;
        if (age < ChangelogWarning) return null;
        var basis = lastSuccess == null ? "registration" : "the last successful changelog sync";
        var message = age >= ChangelogRetention
            ? $"{age.TotalDays:F0} days since {basis}: LinkedIn keeps changelog events for 28 days, so events " +
              "before the retention window may be permanently missing from this database."
            : $"{age.TotalDays:F0} days since {basis}: sync within {ChangelogRetention.TotalDays - age.TotalDays:F0} " +
              "days to avoid losing changelog events.";
        return new ChangelogGap(MemberDatabase.Timestamp(since.Value), Math.Round(age.TotalDays, 1), message);
    }

    internal static ChangelogEventRow EventRow(JsonElement e, long runId) => new()
    {
        EventId = e.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String
            ? id.ToString()
            : "sha256:" + Hash(Canonical(e)),
        ActivityId = Text(e, "activityId"),
        ResourceName = Text(e, "resourceName"),
        ResourceId = Text(e, "resourceId"),
        Method = Text(e, "method"),
        ActivityStatus = Text(e, "activityStatus"),
        Owner = Text(e, "owner"),
        Actor = Text(e, "actor"),
        CapturedAt = Millis(e, "capturedAt"),
        ProcessedAt = Millis(e, "processedAt"),
        Json = e.GetRawText(),
        FirstSeenAt = MemberDatabase.Timestamp(DateTimeOffset.UtcNow),
        RunId = runId,
    };

    // Snapshot

    private sealed class DomainState
    {
        public int Pages;
        public int Records;
        public int Added;
        public readonly Dictionary<string, int> Occurrences = new();
    }

    /// <summary>
    /// One full snapshot pass (all domains, or one), then reconciliation. Records missing from a domain are only
    /// tombstoned after the whole pass completed and that domain returned data in it.
    /// </summary>
    internal async Task<SnapshotResult> SyncSnapshot(string? domain, string mode, Action<string> progress,
        CancellationToken ct)
    {
        var dao = database.Dao;
        var runId = await dao.InsertRun("snapshot", mode, domain, Now);
        var seen = new Dictionary<string, DomainState>(StringComparer.Ordinal);
        string? error = null;
        var complete = false;

        progress(domain == null ? "Snapshot: reading all domains…" : $"Snapshot: reading {domain}…");
        try
        {
            await ReadPass(domain, runId, seen, progress, ct);
            complete = true;
        }
        catch (MemberDataApiException e) when (domain != null && e.IsUnsupportedDomain)
        {
            complete = true;
            await SaveDomain(domain, "unsupported", null, null, e.ApiMessage);
            var unsupported = new SnapshotResult(mode, "success", true,
                [new DomainResult(domain, "unsupported", 0, 0, 0, 0, e.ApiMessage)], null);
            await dao.FinishRun(runId, Now, "success", Details(unsupported));
            return unsupported;
        }
        catch (Exception e) when (e is not OperationCanceledException && !IsAuthFailure(e))
        {
            error = Sanitise(e);
        }
        catch (Exception e) when (IsAuthFailure(e))
        {
            await dao.FinishRun(runId, Now, "failed", Details(new { error = Sanitise(e) }));
            throw;
        }

        var results = new List<DomainResult>();
        foreach (var (name, state) in seen)
        {
            var removed = 0;
            if (complete)
            {
                await database.InTransaction(async t => removed = await dao.TombstoneUnseen(name, runId, Now, t));
                await SaveDomain(name, "synced", state.Pages, state.Records, null, success: true);
            }
            else
            {
                await SaveDomain(name, "incomplete", state.Pages, state.Records, error);
            }

            results.Add(new DomainResult(name, complete ? "synced" : "incomplete", state.Pages, state.Records,
                state.Added, removed, complete ? null : error));
        }

        if (complete && domain == null)
        {
            // Documented domains absent from the all-domain pass are probed one by one, which tells "no data" from
            // "unsupported" and catches anything the combined query left out.
            var counts = (await dao.CountRecords()).ToDictionary(r => r.Domain, r => r.Live);
            var known = (await dao.ListDomains()).Select(d => d.Domain);
            foreach (var name in SnapshotDomains.Documented.Concat(known).Concat(counts.Keys).Distinct()
                         .Where(n => !seen.ContainsKey(n)).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await Probe(name, runId, counts.GetValueOrDefault(name), progress, ct));
            }
        }
        else if (complete && domain != null && !seen.ContainsKey(domain))
        {
            var live = (await dao.CountRecords()).FirstOrDefault(r => r.Domain == domain)?.Live ?? 0;
            var status = live > 0 ? "not_returned" : "no_data";
            await SaveDomain(domain, status, 0, (int)live, null);
            results.Add(new DomainResult(domain, status, 0, (int)live, 0, 0, null));
        }

        var failedProbes = results.Any(r => r.Status == "failed");
        var overall = !complete ? (seen.Count > 0 ? "partial" : "failed") : failedProbes ? "partial" : "success";
        if (complete && domain == null) await dao.SetCheckpoint(SnapshotLastFullSuccess, Now, Now);

        var result = new SnapshotResult(mode, overall, complete,
            results.OrderBy(r => r.Domain, StringComparer.Ordinal).ToList(), error);
        await dao.FinishRun(runId, Now, overall, Details(result));
        return result;
    }

    private async Task ReadPass(string? domain, long runId, Dictionary<string, DomainState> seen,
        Action<string> progress, CancellationToken ct)
    {
        var dao = database.Dao;
        await foreach (var page in client.GetSnapshot(domain, ct))
        {
            if (!seen.TryGetValue(page.Domain, out var state)) seen[page.Domain] = state = new DomainState();
            state.Pages++;
            var now = Now;
            await database.InTransaction(async t =>
            {
                foreach (var record in page.Records)
                {
                    var json = Canonical(record);
                    var hash = Hash(json);
                    // Identical records are legitimate (repeated messages, duplicated rows); number the repeats.
                    var occurrence = state.Occurrences[hash] = state.Occurrences.GetValueOrDefault(hash) + 1;
                    var key = hash[..32] + (occurrence > 1 ? "-" + occurrence.ToString(CultureInfo.InvariantCulture) : "");
                    var added = await dao.UpsertRecord(new SnapshotRecordRow
                    {
                        Domain = page.Domain, RecordKey = key, Json = record.GetRawText(), ContentHash = hash,
                        FirstSeenAt = now, LastSeenAt = now, FirstRunId = runId, LastRunId = runId,
                    }, t);
                    state.Records++;
                    if (added == 1) state.Added++;
                }
            });
            progress($"  {page.Domain} page {page.Start}: {page.Records.Count} records");
        }
    }

    private async Task<DomainResult> Probe(string name, long runId, long liveRecords, Action<string> progress,
        CancellationToken ct)
    {
        var seen = new Dictionary<string, DomainState>(StringComparer.Ordinal);
        try
        {
            await ReadPass(name, runId, seen, _ => { }, ct);
        }
        catch (MemberDataApiException e) when (e.IsUnsupportedDomain)
        {
            await SaveDomain(name, "unsupported", 0, (int)liveRecords, e.ApiMessage);
            return new DomainResult(name, "unsupported", 0, (int)liveRecords, 0, 0, e.ApiMessage);
        }
        catch (Exception e) when (e is not OperationCanceledException && !IsAuthFailure(e))
        {
            var message = Sanitise(e);
            await SaveDomain(name, "failed", null, null, message);
            return new DomainResult(name, "failed", 0, 0, 0, 0, message);
        }

        if (seen.TryGetValue(name, out var state))
        {
            var removed = 0;
            await database.InTransaction(async t => removed = await database.Dao.TombstoneUnseen(name, runId, Now, t));
            await SaveDomain(name, "synced", state.Pages, state.Records, null, success: true);
            progress($"  {name}: {state.Records} records (not in the combined query)");
            return new DomainResult(name, "synced", state.Pages, state.Records, state.Added, removed, null);
        }

        // No data now. Stored records are kept: an empty answer is not proof of deletion.
        var status = liveRecords > 0 ? "not_returned" : "no_data";
        await SaveDomain(name, status, 0, (int)liveRecords, null);
        return new DomainResult(name, status, 0, (int)liveRecords, 0, 0, null);
    }

    private Task SaveDomain(string name, string status, int? pages, int? records, string? error, bool success = false) =>
        database.Dao.SaveDomain(new SnapshotDomainRow
        {
            Domain = name, Documented = SnapshotDomains.Documented.Contains(name), Status = status, Pages = pages,
            Records = records, LastAttemptAt = Now, LastSuccessAt = success ? Now : null, LastError = error,
        });

    // Helpers

    /// <summary>JSON with object keys sorted recursively, so the same record always hashes the same.</summary>
    internal static string Canonical(JsonElement element) => Sort(JsonNode.Parse(element.GetRawText()))?
        .ToJsonString() ?? "null";

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sort(p.Value?.DeepClone())))),
        JsonArray a => new JsonArray(a.Select(i => Sort(i?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };

    internal static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText() : null;

    private static long? Millis(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static bool IsAuthFailure(Exception e) =>
        e is MemberDataApiException { IsAuthentication: true } or MemberDataApiException { IsPermission: true };

    /// <summary>Exception text for storage: the API's message or the exception type, never request headers.</summary>
    private static string Sanitise(Exception e) => e switch
    {
        MemberDataApiException api => api.Message,
        HttpRequestException http => "Network error: " + (http.HttpRequestError.ToString()),
        TaskCanceledException => "Request timed out",
        _ => e.GetType().Name + ": " + e.Message,
    };

    private static string Details(object value) => JsonSerializer.Serialize(value, Output.JsonOptions);
}
