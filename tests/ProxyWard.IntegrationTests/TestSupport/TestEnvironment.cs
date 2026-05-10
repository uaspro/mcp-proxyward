namespace ProxyWard.IntegrationTests;

internal sealed class TestEnvironment : IDisposable
{
    private readonly Dictionary<string, string?> _originalValues = new(StringComparer.Ordinal);
    private readonly Stack<string> _changedNames = new();

    private TestEnvironment()
    {
    }

    public static TestEnvironment Create() => new();

    public TestEnvironment Set(string name, string? value)
    {
        if (!_originalValues.ContainsKey(name))
        {
            _originalValues[name] = Environment.GetEnvironmentVariable(name);
            _changedNames.Push(name);
        }

        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public void Dispose()
    {
        while (_changedNames.TryPop(out var name))
        {
            Environment.SetEnvironmentVariable(name, _originalValues[name]);
        }
    }
}
