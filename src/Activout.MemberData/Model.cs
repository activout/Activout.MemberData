using System.Text.Json;

namespace Activout.MemberData;

/// <summary>One page of the Member Snapshot API: a single domain and its records as returned (raw JSON).</summary>
/// <param name="Start">The page index requested (the API's <c>start</c> parameter counts pages, not records).</param>
/// <param name="Total">The API's <c>paging.total</c>, which LinkedIn documents as possibly inaccurate.</param>
public sealed record SnapshotPage(string Domain, int Start, int? Total, IReadOnlyList<JsonElement> Records);

/// <summary>One page of the Member Changelog API. Events are kept as raw JSON.</summary>
public sealed record ChangelogPage(IReadOnlyList<JsonElement> Events);

/// <summary>The member's DMA authorisation as reported by <c>/rest/memberAuthorizations</c>.</summary>
/// <param name="RegulatedAt">Since when LinkedIn archives changelog events for this member and application.</param>
public sealed record MemberAuthorization(string Member, string DeveloperApplication, DateTimeOffset? RegulatedAt,
    IReadOnlyList<string> Scopes);
