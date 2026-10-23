namespace Activout.MemberData.Tests;

/// <summary>Synthetic fixtures from examples/synthetic-fixtures, copied next to the test assembly.</summary>
internal static class Fixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));
}
