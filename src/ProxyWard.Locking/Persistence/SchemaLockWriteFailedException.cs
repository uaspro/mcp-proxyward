namespace ProxyWard.Locking.Persistence;

public sealed class SchemaLockWriteFailedException : Exception
{
    public SchemaLockWriteFailedException(string reason, string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public string Reason { get; }
}
