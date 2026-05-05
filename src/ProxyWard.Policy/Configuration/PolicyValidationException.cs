namespace ProxyWard.Policy.Configuration;

public sealed class PolicyValidationException : Exception
{
    public PolicyValidationException(IReadOnlyCollection<string> errors)
        : base($"Invalid ProxyWard policy: {string.Join("; ", errors)}")
    {
        Errors = errors;
    }

    public IReadOnlyCollection<string> Errors { get; }
}
