namespace ProxyWard.Policy.Configuration;

public enum ProxyWardMode
{
    Audit,
    Enforce
}

public enum ToolDefaultMode
{
    Allow,
    Deny,
    Hide
}

public enum UnsupportedInspectionBehavior
{
    Warn,
    Block,
    PassThrough
}
