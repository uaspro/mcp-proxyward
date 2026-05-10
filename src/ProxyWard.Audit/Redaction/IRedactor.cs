namespace ProxyWard.Audit.Redaction;

public interface IRedactor
{
    RedactedValue Redact(string path, object? value);

    RedactedValue Redact(string path, object? value, SecretRedactionOptions? secretOptions);
}

public sealed record SecretRedactionOptions(
    bool RedactInLogs,
    IReadOnlyCollection<string> Patterns);
