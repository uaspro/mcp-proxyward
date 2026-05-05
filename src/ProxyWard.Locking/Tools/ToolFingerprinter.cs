using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProxyWard.Locking.Tools;

public sealed class ToolFingerprinter : IToolFingerprinter
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = false
    };

    public ToolFingerprint Fingerprint(DiscoveredTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        return new ToolFingerprint(
            HashString(tool.Name),
            HashString(tool.Title),
            HashString(NormalizeDescription(tool.Description)),
            HashJson(tool.InputSchema),
            HashJson(tool.OutputSchema));
    }

    private static string? NormalizeDescription(string? description) =>
        description?.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

    private static string? HashString(string? value) =>
        value is null ? null : HashBytes(Encoding.UTF8.GetBytes(value));

    private static string? HashJson(JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        var canonical = Canonicalize(value);
        return HashString(canonical.ToJsonString(CanonicalJsonOptions));
    }

    private static JsonNode Canonicalize(JsonNode value) =>
        value switch
        {
            JsonObject jsonObject => CanonicalizeObject(jsonObject),
            JsonArray jsonArray => CanonicalizeArray(jsonArray),
            _ => value.DeepClone()
        };

    private static JsonObject CanonicalizeObject(JsonObject jsonObject)
    {
        var canonical = new JsonObject();
        foreach (var property in jsonObject.OrderBy(property => property.Key, StringComparer.Ordinal))
        {
            canonical[property.Key] = property.Value is null
                ? null
                : Canonicalize(property.Value);
        }

        return canonical;
    }

    private static JsonArray CanonicalizeArray(JsonArray jsonArray)
    {
        var canonical = new JsonArray();
        foreach (var item in jsonArray)
        {
            canonical.Add(item is null ? null : Canonicalize(item));
        }

        return canonical;
    }

    private static string HashBytes(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
