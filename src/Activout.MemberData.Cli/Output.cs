using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Activout.MemberData.Cli;

/// <summary>Documented exit codes; see docs/troubleshooting.md.</summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int Unexpected = 1;
    public const int Usage = 2;
    public const int AuthRequired = 3;
    public const int ApiOrNetwork = 4;
    public const int PartialSync = 5;
    public const int Busy = 6;
    public const int Database = 7;
    public const int UnsafeQuery = 8;
    public const int Cancelled = 130;

    public static int For(Exception e) => e switch
    {
        MemberUsageException => Usage,
        MemberAuthException or CredentialStoreUnavailableException => AuthRequired,
        MemberDataApiException { IsAuthentication: true } or MemberDataApiException { IsPermission: true } => AuthRequired,
        MemberDataApiException or HttpRequestException or SocketException or TimeoutException => ApiOrNetwork,
        SyncLockedException => Busy,
        MemberDatabaseException or Microsoft.Data.Sqlite.SqliteException => Database,
        UnsafeQueryException => UnsafeQuery,
        _ => Unexpected,
    };

    public static string Name(int code) => code switch
    {
        Success => "success",
        Usage => "usage",
        AuthRequired => "auth_required",
        ApiOrNetwork => "api_or_network",
        PartialSync => "partial_sync",
        Busy => "busy",
        Database => "database",
        UnsafeQuery => "unsafe_query",
        Cancelled => "cancelled",
        _ => "unexpected",
    };
}

/// <summary>
/// Writes results. JSON mode prints exactly one document on stdout: <c>{"ok", "exit_code", "result"}</c> on success
/// and <c>{"ok": false, "exit_code", "error": {"type", "message"}}</c> on failure. Diagnostics always go to stderr.
/// </summary>
internal sealed class Output(TextWriter stdout, TextWriter stderr, bool json)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public bool Json => json;
    public TextWriter Out => stdout;

    public void Progress(string message) => stderr.WriteLine(message);

    public void Result(object? value, Action human, int exitCode = ExitCodes.Success)
    {
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new { ok = exitCode == 0, exit_code = exitCode, result = value },
                JsonOptions));
        }
        else
        {
            human();
        }
    }

    public void Error(Exception e, int exitCode)
    {
        stderr.WriteLine($"member: {e.Message}");
        if (exitCode == ExitCodes.Unexpected) stderr.WriteLine(e);
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                ok = false, exit_code = exitCode, error = new { type = ExitCodes.Name(exitCode), message = e.Message },
            }, JsonOptions));
        }
    }

    public void Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var all = rows.Select(r => r.Select(c => c ?? "").ToList()).ToList();
        var widths = headers.Select((h, i) => Math.Max(h.Length, all.Count == 0 ? 0 : all.Max(r => r[i].Length)))
            .ToList();
        stdout.WriteLine(Line(headers));
        foreach (var row in all) stdout.WriteLine(Line(row));
        return;

        string Line(IReadOnlyList<string> cells) =>
            string.Join("  ", cells.Select((c, i) => i == cells.Count - 1 ? c : c.PadRight(widths[i]))).TrimEnd();
    }
}
