namespace ProxyWard.Core.Policies;

public sealed record PolicyDecision(
    PolicyDecisionType Type,
    IReadOnlyCollection<string> Reasons)
{
    public static PolicyDecision Allow() => new(PolicyDecisionType.Allow, []);

    public static PolicyDecision Block(params string[] reasons) =>
        new(PolicyDecisionType.Block, reasons);

    public static PolicyDecision WouldBlock(params string[] reasons) =>
        new(PolicyDecisionType.WouldBlock, reasons);
}

public enum PolicyDecisionType
{
    Allow,
    Warn,
    WouldBlock,
    Block
}
