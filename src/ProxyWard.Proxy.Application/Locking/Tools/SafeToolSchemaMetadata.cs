using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProxyWard.Locking.Persistence;

namespace ProxyWard.Locking.Tools;

public static class SafeToolSchemaMetadata
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false
    };

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.Ordinal)
    {
        "token",
        "secret",
        "password",
        "passwd",
        "apikey",
        "authorization",
        "bearer",
        "accesstoken",
        "refreshtoken",
        "bearertoken",
        "authtoken",
        "secretkey",
        "clientsecret",
        "xapikey"
    };

    private static readonly HashSet<string> ValueBearingKeys = new(StringComparer.Ordinal)
    {
        "default",
        "example",
        "examples",
        "const",
        "enum"
    };

    public static ToolSchemaSnapshotEntry CreateSnapshotEntry(
        DiscoveredTool tool,
        IToolFingerprinter fingerprinter,
        ToolSchemaDiffMetadataOptions options)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(fingerprinter);
        ArgumentNullException.ThrowIfNull(options);

        var normalized = options.Normalize();
        var fingerprint = fingerprinter.Fingerprint(tool);
        if (!normalized.CaptureValues)
        {
            return new ToolSchemaSnapshotEntry(tool.Name!, fingerprint);
        }

        return new ToolSchemaSnapshotEntry(
            tool.Name!,
            fingerprint,
            Title: FitString(tool.Title, normalized.MaxValueBytes),
            Description: FitString(tool.Description, normalized.MaxValueBytes),
            InputSchemaJson: CreateSchemaJson(tool.InputSchema, normalized.MaxValueBytes),
            OutputSchemaJson: CreateSchemaJson(tool.OutputSchema, normalized.MaxValueBytes));
    }

    public static string? CreateDescriptionJson(
        ToolSchemaSnapshotEntry? entry,
        ToolSchemaDiffMetadataOptions options)
    {
        if (entry is null || !options.Normalize().CaptureValues)
        {
            return null;
        }

        if (entry.Title is null && entry.Description is null)
        {
            return null;
        }

        var json = new JsonObject
        {
            ["title"] = entry.Title,
            ["description"] = entry.Description
        }.ToJsonString(CompactJson);

        return Fits(json, options.Normalize().MaxValueBytes) ? json : null;
    }

    public static string? CreateSchemaJson(
        ToolSchemaSnapshotEntry? entry,
        ToolSchemaDiffMetadataOptions options)
    {
        if (entry is null || !options.Normalize().CaptureValues)
        {
            return null;
        }

        if (entry.InputSchemaJson is null && entry.OutputSchemaJson is null)
        {
            return null;
        }

        var json = new JsonObject
        {
            ["inputSchema"] = ParseStoredJson(entry.InputSchemaJson),
            ["outputSchema"] = ParseStoredJson(entry.OutputSchemaJson)
        }.ToJsonString(CompactJson);

        return Fits(json, options.Normalize().MaxValueBytes) ? json : null;
    }

    public static string HashDescription(ToolSchemaSnapshotEntry? entry) =>
        HashParts("description", entry?.Fingerprint.TitleHash, entry?.Fingerprint.DescriptionHash);

    public static string HashSchema(ToolSchemaSnapshotEntry? entry) =>
        HashParts("schema", entry?.Fingerprint.InputSchemaHash, entry?.Fingerprint.OutputSchemaHash);

    public static string HashText(string? value) =>
        HashParts("text", value);

    private static string? CreateSchemaJson(JsonNode? schema, int maxValueBytes)
    {
        if (schema is null)
        {
            return null;
        }

        var redacted = CanonicalizeAndRedact(schema, path: string.Empty, parentKey: string.Empty);
        var json = redacted.ToJsonString(CompactJson);
        return Fits(json, maxValueBytes) ? json : null;
    }

    private static JsonNode CanonicalizeAndRedact(JsonNode node, string path, string parentKey) =>
        node switch
        {
            JsonObject jsonObject => CanonicalizeObject(jsonObject, path),
            JsonArray jsonArray => CanonicalizeArray(jsonArray, path, parentKey),
            JsonValue jsonValue => RedactValue(jsonValue, path, parentKey),
            _ => node.DeepClone()
        };

    private static JsonObject CanonicalizeObject(JsonObject source, string path)
    {
        var result = new JsonObject();
        foreach (var property in source.OrderBy(property => property.Key, StringComparer.Ordinal))
        {
            var childPath = string.IsNullOrEmpty(path) ? property.Key : $"{path}.{property.Key}";
            result[property.Key] = property.Value is null
                ? null
                : CanonicalizeAndRedact(property.Value, childPath, property.Key);
        }

        return result;
    }

    private static JsonArray CanonicalizeArray(JsonArray source, string path, string parentKey)
    {
        var result = new JsonArray();
        for (var index = 0; index < source.Count; index++)
        {
            result.Add(source[index] is null
                ? null
                : CanonicalizeAndRedact(source[index]!, $"{path}[{index}]", parentKey));
        }

        return result;
    }

    private static JsonNode RedactValue(JsonValue value, string path, string parentKey)
    {
        if (!value.TryGetValue<string>(out var text) || text is null)
        {
            return value.DeepClone();
        }

        if (ShouldRedact(path, parentKey, text))
        {
            return JsonValue.Create("[redacted]");
        }

        return JsonValue.Create(text);
    }

    private static bool ShouldRedact(string path, string parentKey, string value)
    {
        var normalizedPathKey = NormalizeKey(GetLastPathSegmentName(path));
        var normalizedParentKey = NormalizeKey(parentKey);

        return SensitiveKeys.Contains(normalizedPathKey)
            || SensitiveKeys.Contains(normalizedParentKey)
            || ValueBearingKeys.Contains(normalizedPathKey)
            || ValueBearingKeys.Contains(normalizedParentKey)
            || LooksLikeSecretAssignment(value);
    }

    private static bool LooksLikeSecretAssignment(string value)
    {
        var normalized = value.ToLowerInvariant();
        return normalized.Contains("token=", StringComparison.Ordinal)
            || normalized.Contains("password=", StringComparison.Ordinal)
            || normalized.Contains("secret=", StringComparison.Ordinal)
            || normalized.Contains("apikey=", StringComparison.Ordinal)
            || normalized.Contains("api_key=", StringComparison.Ordinal);
    }

    private static string? FitString(string? value, int maxValueBytes)
    {
        if (value is null)
        {
            return null;
        }

        return Fits(value, maxValueBytes) ? value : null;
    }

    private static bool Fits(string value, int maxValueBytes) =>
        Encoding.UTF8.GetByteCount(value) <= maxValueBytes;

    private static JsonNode? ParseStoredJson(string? json) =>
        json is null ? null : JsonNode.Parse(json);

    private static string HashParts(params string?[] parts)
    {
        var joined = string.Join('\u001f', parts.Select(part => part ?? string.Empty));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string GetLastPathSegmentName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var lastSegment = path;
        var lastDot = path.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < path.Length - 1)
        {
            lastSegment = path[(lastDot + 1)..];
        }

        var arrayStart = lastSegment.IndexOf('[');
        if (arrayStart > 0)
        {
            lastSegment = lastSegment[..arrayStart];
        }

        return lastSegment;
    }

    private static string NormalizeKey(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return string.Empty;
        }

        var buffer = new char[segment.Length];
        var length = 0;

        foreach (var ch in segment)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[length++] = char.ToLowerInvariant(ch);
            }
        }

        return new string(buffer, 0, length);
    }
}
