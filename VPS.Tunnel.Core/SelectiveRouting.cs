using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VPS.Tunnel.Core;

/// <summary>What SELECTIVE sends through the VPS: the listed Windows programs and, optionally, all of WSL.</summary>
public sealed record SelectiveRequest(IReadOnlyList<string> Applications, bool IncludeWsl)
{
    public bool IsEmpty => Applications.Count == 0 && !IncludeWsl;
}

public static partial class SelectiveApplications
{
    public const int MaxCount = 64;
    public const string StartArgument = "--selective";
    public const string WslArgument = "--wsl";

    // A bare Windows executable file name: no path, no wildcard, no control characters.
    [GeneratedRegex(@"^[^\\/:*?""<>|\x00-\x1F]{1,120}\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static bool IsValidName(string? name) =>
        name != null && name == name.Trim() && !name.StartsWith('.') && NamePattern().IsMatch(name);

    /// <summary>Trims, drops invalid names and case-insensitive duplicates, and caps the count.</summary>
    public static List<string> Normalize(IEnumerable<string?>? names) =>
        (names ?? [])
            .Select(name => name?.Trim())
            .Where(IsValidName)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCount)
            .ToList();

    /// <summary>Service start arguments: the marker, the optional WSL flag, then one executable name per argument.</summary>
    public static string[] ToStartArguments(SelectiveRequest request) =>
        [StartArgument, .. request.IncludeWsl ? new[] { WslArgument } : [], .. request.Applications];

    /// <summary>
    /// Returns null when the service was started without the selective marker (full TUNNEL).
    /// Throws when the marker is present but nothing is selected or a name is invalid.
    /// </summary>
    public static SelectiveRequest? ParseStartArguments(IReadOnlyList<string>? args)
    {
        if (args == null) return null;
        var marker = -1;
        for (var i = 0; i < args.Count; i++)
            if (args[i] == StartArgument) { marker = i; break; }
        if (marker < 0) return null;
        var rest = args.Skip(marker + 1).ToList();
        var includeWsl = rest.Remove(WslArgument);
        if (rest.Count > MaxCount || rest.Any(name => !IsValidName(name)) || rest.Count == 0 && !includeWsl)
            throw new ArgumentException("Invalid selective application list.");
        return new(rest.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), includeWsl);
    }
}

/// <summary>
/// Derives the SELECTIVE sing-box configuration from the verified TUNNEL configuration:
/// listed processes (and optionally WSL) are routed to the existing proxy outbound,
/// everything else goes direct. The base configuration is never modified; only the
/// route section of the copy changes.
/// </summary>
public static class SelectiveConfigBuilder
{
    public const string AddedDirectTag = "vps-tunnel-direct";
    // WSL 2 in NAT mode picks its virtual network from this private range. Windows' own
    // traffic reaches sing-box from the TUN address, which can be in the same range and is excluded.
    public const string WslSourceRange = "172.16.0.0/12";
    private static readonly HashSet<string> NonProxyTypes = new(StringComparer.OrdinalIgnoreCase)
        { "direct", "block", "dns" };
    private static readonly JsonDocumentOptions ParseOptions = new()
        { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string Build(string baseConfiguration, SelectiveRequest request)
    {
        if (request.IsEmpty || request.Applications.Any(name => !SelectiveApplications.IsValidName(name)))
            throw new ArgumentException("Invalid selective request.", nameof(request));

        if (JsonNode.Parse(baseConfiguration, documentOptions: ParseOptions) is not JsonObject root)
            throw new InvalidDataException("Configuration root is not an object.");
        if (root["outbounds"] is not JsonArray outbounds)
            throw new InvalidDataException("Configuration has no outbounds.");

        var route = root["route"] as JsonObject;
        if (route == null)
        {
            route = new JsonObject();
            root["route"] = route;
        }

        var proxyTag = FindProxyTag(outbounds, route);
        var directTag = FindOrAddDirectTag(outbounds);

        if (route["rules"] is not JsonArray rules)
        {
            rules = new JsonArray();
            route["rules"] = rules;
        }
        if (request.Applications.Count > 0)
        {
            var processNames = new JsonArray();
            foreach (var name in request.Applications) processNames.Add(name);
            rules.Add(new JsonObject { ["process_name"] = processNames, ["outbound"] = proxyTag });
        }
        if (request.IncludeWsl)
        {
            var tunAddresses = new JsonArray();
            foreach (var address in TunAddresses(root)) tunAddresses.Add(address);
            if (tunAddresses.Count == 0) throw new InvalidDataException("TUN inbound address not found.");
            rules.Add(new JsonObject
            {
                ["type"] = "logical",
                ["mode"] = "and",
                ["rules"] = new JsonArray(
                    new JsonObject { ["source_ip_cidr"] = new JsonArray(WslSourceRange) },
                    new JsonObject { ["source_ip_cidr"] = tunAddresses, ["invert"] = true }),
                ["outbound"] = proxyTag
            });
        }

        route["final"] = directTag;
        // Direct traffic leaves through the TUN; without this it would loop back into it.
        route["auto_detect_interface"] = true;
        return root.ToJsonString(WriteOptions);
    }

    private static string FindProxyTag(JsonArray outbounds, JsonObject route)
    {
        // In the verified TUNNEL configuration everything ends up at route.final, so that is the proxy.
        var final = route["final"]?.GetValue<string>();
        if (final != null && outbounds.OfType<JsonObject>().Any(outbound =>
                Tag(outbound) == final && !NonProxyTypes.Contains(Type(outbound))))
            return final;
        // Without route.final sing-box uses the first outbound.
        var first = outbounds.OfType<JsonObject>().FirstOrDefault();
        if (final == null && first != null && Tag(first) is { } firstTag && !NonProxyTypes.Contains(Type(first)))
            return firstTag;
        throw new InvalidDataException("Proxy outbound not found.");
    }

    private static string FindOrAddDirectTag(JsonArray outbounds)
    {
        var existing = outbounds.OfType<JsonObject>()
            .FirstOrDefault(outbound => string.Equals(Type(outbound), "direct", StringComparison.OrdinalIgnoreCase) &&
                Tag(outbound) != null);
        if (existing != null) return Tag(existing)!;
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = AddedDirectTag });
        return AddedDirectTag;
    }

    /// <summary>Addresses of the TUN inbound: "address" (sing-box 1.10+) or the legacy inet4/inet6 fields.</summary>
    private static IEnumerable<string> TunAddresses(JsonObject root)
    {
        if (root["inbounds"] is not JsonArray inbounds) yield break;
        foreach (var inbound in inbounds.OfType<JsonObject>().Where(inbound =>
                     string.Equals(Type(inbound), "tun", StringComparison.OrdinalIgnoreCase)))
            foreach (var field in new[] { "address", "inet4_address", "inet6_address" })
                switch (inbound[field])
                {
                    case JsonValue value when value.TryGetValue<string>(out var single):
                        yield return single;
                        break;
                    case JsonArray list:
                        foreach (var item in list)
                            if (item is JsonValue entry && entry.TryGetValue<string>(out var address))
                                yield return address;
                        break;
                }
    }

    private static string? Tag(JsonObject outbound) =>
        outbound["tag"] is JsonValue value && value.TryGetValue<string>(out var tag) ? tag : null;

    private static string Type(JsonObject outbound) =>
        outbound["type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : "";
}
