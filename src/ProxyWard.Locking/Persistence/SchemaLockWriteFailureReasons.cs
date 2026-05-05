namespace ProxyWard.Locking.Persistence;

public static class SchemaLockWriteFailureReasons
{
    public const string DbLocked = "db_locked";
    public const string DbReadonly = "db_readonly";
    public const string DbIo = "db_io";
}
