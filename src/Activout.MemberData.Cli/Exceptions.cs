namespace Activout.MemberData.Cli;

/// <summary>Invalid arguments or an operation the selected mode does not allow.</summary>
internal sealed class MemberUsageException(string message) : Exception(message);

/// <summary>No usable token, or LinkedIn rejected it. The owner must import a new token.</summary>
internal sealed class MemberAuthException(string message) : Exception(message);

/// <summary>The database is corrupt, too new, or belongs to another member.</summary>
internal sealed class MemberDatabaseException(string message) : Exception(message);

/// <summary>A query was rejected because it is not a single read-only SELECT.</summary>
internal sealed class UnsafeQueryException(string message) : Exception(message);

/// <summary>Another process is synchronising the same database.</summary>
internal sealed class SyncLockedException(string message) : Exception(message);
