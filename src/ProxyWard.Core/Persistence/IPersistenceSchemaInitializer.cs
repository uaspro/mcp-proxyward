namespace ProxyWard.Core.Persistence;

public interface IPersistenceSchemaInitializer
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);
}
