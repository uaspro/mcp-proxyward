using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ProxyWard.Core.Policies;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Policy.Engine;

public sealed class HostArgumentRuleEvaluator(IHostResolver resolver)
{
    public async ValueTask<PolicyDecision> EvaluateAsync(
        ProxyWardMode mode,
        HostArgumentPolicy hosts,
        JsonNode? toolCallParams,
        CancellationToken cancellationToken)
    {
        if (hosts.Allow.Count == 0 && !hosts.BlockPrivateNetworks)
        {
            return PolicyDecision.Allow();
        }

        if (toolCallParams is null)
        {
            return PolicyDecision.Allow();
        }

        var scanRoot = ResolveScanRoot(toolCallParams);
        var candidates = new List<string>();
        Collect(scanRoot, candidates);

        if (candidates.Count == 0)
        {
            return PolicyDecision.Allow();
        }

        var hasNotAllowed = false;
        var hasPrivate = false;

        foreach (var raw in candidates)
        {
            if (TryGetCandidateHost(raw, out var bareHost, out var ipLiteral) is false)
            {
                continue;
            }

            if (hosts.Allow.Count > 0 && !IsInAllowlist(bareHost, hosts.Allow))
            {
                hasNotAllowed = true;
            }

            if (hosts.BlockPrivateNetworks && !hasPrivate)
            {
                hasPrivate = await IsPrivateAsync(bareHost, ipLiteral, cancellationToken).ConfigureAwait(false);
            }

            if (hasNotAllowed && (!hosts.BlockPrivateNetworks || hasPrivate))
            {
                break;
            }
        }

        if (!hasNotAllowed && !hasPrivate)
        {
            return PolicyDecision.Allow();
        }

        var reasons = new List<string>(2);
        if (hasNotAllowed)
        {
            reasons.Add(PolicyReasonCodes.HostNotAllowed);
        }
        if (hasPrivate)
        {
            reasons.Add(PolicyReasonCodes.PrivateNetworkTarget);
        }

        return mode.AsBlockDecision(reasons);
    }

    private static JsonNode ResolveScanRoot(JsonNode toolCallParams)
    {
        if (toolCallParams is JsonObject obj
            && obj.TryGetPropertyValue("arguments", out var args)
            && args is not null)
        {
            return args;
        }

        return toolCallParams;
    }

    private static void Collect(JsonNode? node, List<string> candidates)
    {
        switch (node)
        {
            case null:
                return;
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    Collect(pair.Value, candidates);
                }
                break;
            case JsonArray arr:
                foreach (var child in arr)
                {
                    Collect(child, candidates);
                }
                break;
            case JsonValue val:
                if (val.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                {
                    candidates.Add(s);
                }
                break;
        }
    }

    private static readonly HashSet<string> NetworkSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "ws", "wss", "ftp", "ftps"
    };

    private static bool TryGetCandidateHost(string raw, out string bareHost, out IPAddress? ipLiteral)
    {
        bareHost = string.Empty;
        ipLiteral = null;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host)
            && NetworkSchemes.Contains(uri.Scheme))
        {
            var host = uri.IdnHost;
            if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
            {
                host = host[1..^1];
            }
            // Strip trailing dot (FQDN form) so allowlist matching doesn't get bypassed
            // by `https://example.com./x` vs allowlist entry `example.com`.
            if (host.Length > 1 && host[^1] == '.')
            {
                host = host[..^1];
            }
            bareHost = host.ToLowerInvariant();
            if (IPAddress.TryParse(bareHost, out var asIp))
            {
                ipLiteral = asIp;
            }
            return true;
        }

        if (TryParseBracketedIpV6(raw, out var bracketIp))
        {
            ipLiteral = bracketIp;
            bareHost = bracketIp.ToString();
            return true;
        }

        if (IPAddress.TryParse(raw, out var ip))
        {
            // Defensive: reject non-canonical IPv4 forms (e.g. "010.0.0.5", "127", "0xa.0.0.5").
            // .NET's parser accepts some legacy syntaxes that would diverge from how a policy
            // author or downstream resolver interprets the same string. Requiring a canonical
            // round-trip avoids parse-semantic mismatches that could bypass private-network rules.
            if (ip.AddressFamily == AddressFamily.InterNetwork
                && !string.Equals(ip.ToString(), raw, StringComparison.Ordinal))
            {
                return false;
            }
            ipLiteral = ip;
            bareHost = ip.ToString();
            return true;
        }

        return false;
    }

    private static bool TryParseBracketedIpV6(string raw, out IPAddress address)
    {
        address = IPAddress.None;
        if (raw.Length < 4 || raw[0] != '[')
        {
            return false;
        }

        var closing = raw.IndexOf(']');
        if (closing < 2)
        {
            return false;
        }

        var inner = raw.Substring(1, closing - 1);
        return IPAddress.TryParse(inner, out address!);
    }

    private static bool IsInAllowlist(string bareHost, IReadOnlyCollection<string> allow)
    {
        foreach (var entry in allow)
        {
            if (string.Equals(entry, bareHost, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private async ValueTask<bool> IsPrivateAsync(
        string bareHost,
        IPAddress? ipLiteral,
        CancellationToken cancellationToken)
    {
        if (ipLiteral is not null)
        {
            return IsPrivateAddress(ipLiteral);
        }

        var resolution = await resolver.ResolveAsync(bareHost, cancellationToken).ConfigureAwait(false);
        if (resolution.ResolutionFailed)
        {
            // Fail-closed: an unresolvable host could be anything. Under
            // BlockPrivateNetworks=true the safer default is to treat it as private.
            return true;
        }

        foreach (var address in resolution.Addresses)
        {
            if (IsPrivateAddress(address))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsPrivateIpV4(address);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IsPrivateIpV6(address);
        }

        return false;
    }

    private static bool IsPrivateIpV4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        // RFC1918: 10.0.0.0/8
        if (bytes[0] == 10) return true;
        // RFC1918: 172.16.0.0/12
        if (bytes[0] == 172 && (bytes[1] & 0xf0) == 16) return true;
        // RFC1918: 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        // Link-local: 169.254.0.0/16
        if (bytes[0] == 169 && bytes[1] == 254) return true;
        return false;
    }

    private static bool IsPrivateIpV6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        // Unique-local: fc00::/7 → high bits 1111 110x
        if ((bytes[0] & 0xfe) == 0xfc)
        {
            return true;
        }

        return false;
    }
}
