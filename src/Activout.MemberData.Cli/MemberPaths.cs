namespace Activout.MemberData.Cli;

/// <summary>Where the database lives: the platform data directory, or an explicit --database path.</summary>
internal static class MemberPaths
{
    public static string DatabasePath(string? overridePath)
    {
        if (!string.IsNullOrEmpty(overridePath)) return Path.GetFullPath(overridePath);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string directory;
        if (OperatingSystem.IsWindows())
        {
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Activout.MemberData");
        }
        else if (OperatingSystem.IsMacOS())
        {
            directory = Path.Combine(home, "Library", "Application Support", "Activout.MemberData");
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            // XDG requires absolute paths; relative values are ignored.
            directory = Path.Combine(
                !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(home, ".local", "share"),
                "activout-memberdata");
        }

        return Path.Combine(directory, "member.db");
    }

    /// <summary>Creates a directory readable only by the owner where the platform supports Unix modes.</summary>
    public static void EnsurePrivateDirectory(string path)
    {
        // ponytail: Unix modes only; Windows relies on the per-user profile ACLs, see docs/security-and-privacy.md.
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
