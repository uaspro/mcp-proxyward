namespace ProxyWard.Policy.Configuration;

public enum ProxyWardMode
{
    Audit,
    Enforce
}

public enum ToolDefaultMode
{
    Allow,
    Deny
}

public enum UnsupportedInspectionBehavior
{
    Warn,
    Block,
    PassThrough
}
