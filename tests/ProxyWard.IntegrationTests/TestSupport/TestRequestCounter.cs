namespace ProxyWard.IntegrationTests;

internal sealed class TestRequestCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);
}
