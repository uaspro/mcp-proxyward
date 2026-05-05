namespace ProxyWard.Audit.Redaction;

public interface IRedactor
{
    RedactedValue Redact(string path, object? value);
}
