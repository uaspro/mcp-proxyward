namespace ProxyWard.Locking.Tools;

public sealed record ToolSchemaDiffMetadataOptions(
    bool CaptureValues = true,
    int MaxValueBytes = 16 * 1024)
{
    public static ToolSchemaDiffMetadataOptions Default { get; } = new();

    public ToolSchemaDiffMetadataOptions Normalize()
    {
        if (MaxValueBytes <= 0)
        {
            return this with { MaxValueBytes = Default.MaxValueBytes };
        }

        return this;
    }
}
