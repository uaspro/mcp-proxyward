namespace ProxyWard.Locking.Tools;

public interface IToolFingerprinter
{
    ToolFingerprint Fingerprint(DiscoveredTool tool);
}
