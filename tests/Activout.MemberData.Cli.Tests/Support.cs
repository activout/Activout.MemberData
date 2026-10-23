namespace Activout.MemberData.Cli.Tests;

internal static class Fixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));
}

internal sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _values = new();

    public bool Unavailable { get; set; }
    public string Name => "test store";

    public string? Get(string key)
    {
        Check();
        return _values.GetValueOrDefault(key);
    }

    public void Set(string key, string value)
    {
        Check();
        _values[key] = value;
    }

    public bool Delete(string key)
    {
        Check();
        return _values.Remove(key);
    }

    private void Check()
    {
        if (Unavailable) throw new CredentialStoreUnavailableException("Synthetic: no secret service");
    }
}

internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
