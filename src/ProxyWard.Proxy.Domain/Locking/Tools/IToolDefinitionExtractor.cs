namespace ProxyWard.Locking.Tools;

public interface IToolDefinitionExtractor
{
    ToolListExtractionResult Extract(ReadOnlyMemory<byte> responseBody);

    ToolListExtractionResult Extract(ReadOnlyMemory<byte> responseBody, string? contentType);
}
