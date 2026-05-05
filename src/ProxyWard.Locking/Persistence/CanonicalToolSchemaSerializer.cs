using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProxyWard.Locking.Persistence;

internal static class CanonicalToolSchemaSerializer
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false
    };

    public static (string CanonicalJson, string SnapshotHash) Serialize(
        string mcpProtocol,
        IReadOnlyCollection<ToolSchemaSnapshotEntry> tools)
    {
        ArgumentNullException.ThrowIfNull(mcpProtocol);
        ArgumentNullException.ThrowIfNull(tools);

        var orderedTools = tools
            .Where(entry => !string.IsNullOrEmpty(entry.ToolName))
            .OrderBy(entry => entry.ToolName, StringComparer.Ordinal)
            .ToArray();

        var toolArray = new JsonArray();
        foreach (var entry in orderedTools)
        {
            toolArray.Add(new JsonObject
            {
                ["name"] = entry.ToolName,
                ["nameHash"] = entry.Fingerprint.NameHash,
                ["titleHash"] = entry.Fingerprint.TitleHash,
                ["descriptionHash"] = entry.Fingerprint.DescriptionHash,
                ["inputSchemaHash"] = entry.Fingerprint.InputSchemaHash,
                ["outputSchemaHash"] = entry.Fingerprint.OutputSchemaHash
            });
        }

        var root = new JsonObject
        {
            ["mcpProtocol"] = mcpProtocol,
            ["tools"] = toolArray
        };

        var canonicalJson = root.ToJsonString(CompactJson);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        var snapshotHash = Convert.ToHexStringLower(hashBytes);

        return (canonicalJson, snapshotHash);
    }

    public static IReadOnlyList<ToolSchemaSnapshotEntry> Deserialize(string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);

        using var document = JsonDocument.Parse(canonicalJson);
        if (!document.RootElement.TryGetProperty("tools", out var toolsElement)
            || toolsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var entries = new List<ToolSchemaSnapshotEntry>(toolsElement.GetArrayLength());
        foreach (var element in toolsElement.EnumerateArray())
        {
            var name = element.GetProperty("name").GetString() ?? string.Empty;
            var fingerprint = new Tools.ToolFingerprint(
                NameHash: ReadOptionalString(element, "nameHash"),
                TitleHash: ReadOptionalString(element, "titleHash"),
                DescriptionHash: ReadOptionalString(element, "descriptionHash"),
                InputSchemaHash: ReadOptionalString(element, "inputSchemaHash"),
                OutputSchemaHash: ReadOptionalString(element, "outputSchemaHash"));

            entries.Add(new ToolSchemaSnapshotEntry(name, fingerprint));
        }

        return entries;
    }

    private static string? ReadOptionalString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.GetString();
    }
}
