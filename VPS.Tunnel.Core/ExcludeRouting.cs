using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VPS.Tunnel.Core;

/// <summary>
/// What EXCLUDE ("всё, кроме выбранных") sends direct: programs given by full path
/// (preferred: different programs can share a file name) or by bare file name.
/// </summary>
public sealed record ExcludeRequest(IReadOnlyList<string> Entries)
{
    public IReadOnlyList<string> Paths => Entries.Where(ExcludedApplications.IsPath).ToList();
    public IReadOnlyList<string> Names => Entries.Where(entry => !ExcludedApplications.IsPath(entry)).ToList();
}

public static partial class ExcludedApplications
{
    public const int MaxCount = 64;
    public const string StartArgument = "--exclude";

    // An absolute local path to an .exe: drive letter, no wildcard or control characters, no "." or ".." segments.
    [GeneratedRegex(@"^[A-Za-z]:(\\[^\\/:*?""<>|\x00-\x1F]+)+\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    public static bool IsPath(string entry) => entry.Contains('\\');

    public static bool IsValidEntry(string? entry)
    {
        if (entry == null || entry != entry.Trim() || entry.Length > 260) return false;
        if (!IsPath(entry)) return SelectiveApplications.IsValidName(entry);
        // Windows silently drops trailing dots and spaces; such segments, "." and ".." are refused.
        return PathPattern().IsMatch(entry) &&
            entry.Split('\\').Skip(1).All(part => part.Trim().Length > 0 && !part.EndsWith(' ') && !part.EndsWith('.'));
    }

    /// <summary>Trims quotes and spaces, drops invalid entries and case-insensitive duplicates, caps the count.</summary>
    public static List<string> Normalize(IEnumerable<string?>? entries) =>
        (entries ?? [])
            .Select(entry => entry?.Trim().Trim('"').Trim())
            .Where(IsValidEntry)
            .Select(entry => entry!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCount)
            .ToList();

    public static string[] ToStartArguments(ExcludeRequest request) => [StartArgument, .. request.Entries];

    /// <summary>Returns null without the marker; throws when it is present but the list is empty or invalid.</summary>
    public static ExcludeRequest? ParseStartArguments(IReadOnlyList<string>? args)
    {
        if (args == null) return null;
        var marker = -1;
        for (var i = 0; i < args.Count; i++)
            if (args[i] == StartArgument) { marker = i; break; }
        if (marker < 0) return null;
        var entries = args.Skip(marker + 1).ToList();
        if (entries.Count == 0 || entries.Count > MaxCount || entries.Any(entry => !IsValidEntry(entry)))
            throw new ArgumentException("Invalid excluded application list.");
        return new(entries.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }
}

/// <summary>
/// Derives the EXCLUDE sing-box configuration from the verified TUNNEL configuration: everything
/// still goes to the proxy (route.final unchanged), the listed programs and their DNS go direct.
/// The base configuration is never modified.
/// </summary>
public static class ExcludeConfigBuilder
{
    public const string AddedLocalDnsTag = "vps-tunnel-local-dns";
    private static readonly HashSet<string> ServiceActions = new(StringComparer.OrdinalIgnoreCase) { "sniff", "hijack-dns" };

    public static string Build(string baseConfiguration, ExcludeRequest request)
    {
        if (request.Entries.Count == 0 || request.Entries.Any(entry => !ExcludedApplications.IsValidEntry(entry)))
            throw new ArgumentException("Invalid exclude request.", nameof(request));

        if (JsonNode.Parse(baseConfiguration, documentOptions: SelectiveConfigBuilder.ParseOptions) is not JsonObject root)
            throw new InvalidDataException("Configuration root is not an object.");
        if (root["outbounds"] is not JsonArray outbounds)
            throw new InvalidDataException("Configuration has no outbounds.");
        if (root["route"] is not JsonObject route)
        {
            route = new JsonObject();
            root["route"] = route;
        }

        var proxyTag = SelectiveConfigBuilder.FindProxyTag(outbounds, route);
        var directTag = SelectiveConfigBuilder.FindOrAddDirectTag(outbounds);

        if (route["rules"] is not JsonArray rules)
        {
            rules = new JsonArray();
            route["rules"] = rules;
        }
        // After the leading service rules (sniff, hijack-dns), before everything else. No network
        // filter, so TCP and UDP of these processes both go direct.
        var position = 0;
        while (position < rules.Count && rules[position] is JsonObject rule &&
               rule["action"] is JsonValue action && action.TryGetValue<string>(out var name) && ServiceActions.Contains(name))
            position++;
        foreach (var (field, values) in Matchers(request))
            rules.Insert(position++, new JsonObject { [field] = ToArray(values), ["outbound"] = directTag });

        // The tunnel stays the default, as in TUNNEL.
        route["final"] = proxyTag;
        // Direct traffic leaves through the TUN; without this it would loop back into it.
        route["auto_detect_interface"] = true;

        AddDirectDns(root, request, directTag);
        return root.ToJsonString(SelectiveConfigBuilder.WriteOptions);
    }

    private static IEnumerable<(string Field, IReadOnlyList<string> Values)> Matchers(ExcludeRequest request)
    {
        if (request.Paths.Count > 0) yield return ("process_path", request.Paths);
        if (request.Names.Count > 0) yield return ("process_name", request.Names);
    }

    /// <summary>DNS queries attributed to the listed processes go to a local (system) resolver.</summary>
    private static void AddDirectDns(JsonObject root, ExcludeRequest request, string directTag)
    {
        if (root["dns"] is not JsonObject dns) return; // no DNS section: queries already use the system resolver
        if (dns["servers"] is not JsonArray servers)
        {
            servers = new JsonArray();
            dns["servers"] = servers;
        }
        var legacy = servers.OfType<JsonObject>().Any(server => server["type"] == null && server["address"] != null);
        var local = servers.OfType<JsonObject>().FirstOrDefault(server =>
            string.Equals(SelectiveConfigBuilder.Type(server), "local", StringComparison.OrdinalIgnoreCase) ||
            server["type"] == null && server["address"] is JsonValue address &&
            address.TryGetValue<string>(out var value) && value == "local");
        var localTag = local != null ? SelectiveConfigBuilder.Tag(local) : null;
        if (localTag == null)
        {
            // With a single DNS server sing-box uses it implicitly to resolve outbound server names; a second
            // server makes route.default_domain_resolver mandatory (sing-box 1.12+). Name the server it used before.
            var previous = dns["final"] is JsonValue final && final.TryGetValue<string>(out var finalTag) ? finalTag
                : servers.OfType<JsonObject>().Select(SelectiveConfigBuilder.Tag).FirstOrDefault(tag => tag != null);
            if (!legacy && previous != null && root["route"] is JsonObject route && route["default_domain_resolver"] == null &&
                servers.Count == 1)
                route["default_domain_resolver"] = previous;
            localTag = AddedLocalDnsTag;
            servers.Add(legacy
                ? new JsonObject { ["tag"] = localTag, ["address"] = "local", ["detour"] = directTag }
                : new JsonObject { ["type"] = "local", ["tag"] = localTag });
        }
        if (dns["rules"] is not JsonArray dnsRules)
        {
            dnsRules = new JsonArray();
            dns["rules"] = dnsRules;
        }
        var position = 0;
        foreach (var (field, values) in Matchers(request))
            dnsRules.Insert(position++, new JsonObject { [field] = ToArray(values), ["server"] = localTag });
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }
}
