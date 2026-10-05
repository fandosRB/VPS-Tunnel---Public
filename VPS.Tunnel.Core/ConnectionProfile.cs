using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VPS.Tunnel.Core;

/// <summary>A user's VLESS + Reality server, entered in the GUI or imported from a vless:// link.</summary>
public sealed record ConnectionProfile(
    string Name,
    string Server,
    int Port,
    string Uuid,
    string ServerName,
    string PublicKey,
    string ShortId,
    string Fingerprint = "chrome",
    string Flow = "xtls-rprx-vision")
{
    public static readonly string[] Fingerprints = ["chrome", "firefox", "edge", "safari", "ios", "android", "random"];

    /// <summary>Returns a Russian error message, or null when the profile can be turned into a configuration.</summary>
    public string? Validate()
    {
        if (!IsHost(Server)) return "Укажите адрес сервера (IP или домен).";
        if (Port is < 1 or > 65535) return "Порт должен быть числом от 1 до 65535.";
        if (!Guid.TryParse(Uuid, out _)) return "UUID указан неверно.";
        if (!IsHost(ServerName)) return "Укажите SNI (имя сайта для маскировки), например www.microsoft.com.";
        if (!ProfilePatterns.PublicKey().IsMatch(PublicKey)) return "Публичный ключ Reality (pbk) указан неверно.";
        if (!ProfilePatterns.ShortId().IsMatch(ShortId)) return "Short ID должен состоять из 0–16 шестнадцатеричных символов.";
        if (!Fingerprints.Contains(Fingerprint)) return "Неизвестный отпечаток браузера (fp).";
        if (Flow is not ("" or "xtls-rprx-vision")) return "Поддерживается только flow xtls-rprx-vision.";
        return null;
    }

    /// <summary>The server address when it is a literal IPv4 address (the expected exit IP).</summary>
    public string? ServerIPv4 =>
        IPAddress.TryParse(Server, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? address.ToString() : null;

    private static bool IsHost(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 253 &&
        (IPAddress.TryParse(value, out _) || ProfilePatterns.DomainName().IsMatch(value));
}

internal static partial class ProfilePatterns
{
    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)*[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?$")]
    public static partial Regex DomainName();
    [GeneratedRegex(@"^[A-Za-z0-9_-]{43}=?$")]
    public static partial Regex PublicKey();
    [GeneratedRegex(@"^[0-9a-fA-F]{0,16}$")]
    public static partial Regex ShortId();
}

/// <summary>Parses the vless:// share links produced by 3x-ui, Marzban, Hiddify and similar panels.</summary>
public static class VlessLink
{
    /// <exception cref="FormatException">With a Russian message when the link is not a usable VLESS + Reality link.</exception>
    public static ConnectionProfile Parse(string link)
    {
        link = link.Trim();
        if (!link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Ссылка должна начинаться с vless://");
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            throw new FormatException("Не удалось разобрать ссылку.");

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            query[Uri.UnescapeDataString(pair[0])] = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
        }
        string Get(string key, string fallback = "") => query.TryGetValue(key, out var value) ? value.Trim() : fallback;

        if (!Get("security").Equals("reality", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Поддерживаются только ссылки VLESS + Reality (security=reality).");
        var transport = Get("type", "tcp");
        if (!transport.Equals("tcp", StringComparison.OrdinalIgnoreCase) && !transport.Equals("raw", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Транспорт «{transport}» не поддерживается, нужен tcp.");

        var server = uri.Host.Trim('[', ']');
        var name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')).Trim();
        var fingerprint = Get("fp", "chrome").ToLowerInvariant();
        var profile = new ConnectionProfile(
            Name: name.Length == 0 ? server : name,
            Server: server,
            Port: uri.IsDefaultPort || uri.Port < 0 ? 443 : uri.Port,
            Uuid: Uri.UnescapeDataString(uri.UserInfo),
            ServerName: Get("sni"),
            PublicKey: Get("pbk"),
            ShortId: Get("sid"),
            Fingerprint: ConnectionProfile.Fingerprints.Contains(fingerprint) ? fingerprint : "chrome",
            Flow: Get("flow"));
        if (profile.Validate() is { } error) throw new FormatException(error);
        return profile;
    }
}

/// <summary>
/// Builds a complete sing-box 1.14 configuration for Windows: TUN for all traffic, VLESS + Reality
/// to the user's server, DNS over HTTPS through the tunnel, local network direct.
/// </summary>
public static class SingBoxConfigTemplate
{
    public const string ProxyTag = "proxy";
    public const string TunInterfaceName = "sing-box-tun"; // the GUI recognises the adapter by "sing-box"

    public static string Build(ConnectionProfile profile)
    {
        if (profile.Validate() is { } error) throw new ArgumentException(error, nameof(profile));
        var vless = new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = ProxyTag,
            ["server"] = profile.Server,
            ["server_port"] = profile.Port,
            ["uuid"] = profile.Uuid,
            ["packet_encoding"] = "xudp",
            ["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = profile.ServerName,
                ["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = profile.Fingerprint },
                ["reality"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["public_key"] = profile.PublicKey,
                    ["short_id"] = profile.ShortId
                }
            }
        };
        if (profile.Flow.Length > 0) vless["flow"] = profile.Flow;

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(
                    new JsonObject { ["type"] = "https", ["tag"] = "remote-dns", ["server"] = "1.1.1.1", ["detour"] = ProxyTag },
                    new JsonObject { ["type"] = "local", ["tag"] = "local-dns" }),
                ["final"] = "remote-dns",
                ["strategy"] = "ipv4_only"
            },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = TunInterfaceName,
                ["address"] = new JsonArray("172.19.0.1/30"),
                ["auto_route"] = true,
                ["strict_route"] = true,
                ["stack"] = "mixed"
            }),
            ["outbounds"] = new JsonArray(vless, new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
            ["route"] = new JsonObject
            {
                ["rules"] = new JsonArray(
                    new JsonObject { ["action"] = "sniff" },
                    new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
                    new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" }),
                ["final"] = ProxyTag,
                ["auto_detect_interface"] = true,
                // Resolves a domain-name server address before the tunnel exists.
                ["default_domain_resolver"] = "local-dns"
            }
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Checks that an imported file is a sing-box configuration this app can drive: a JSON object
    /// with a TUN inbound and outbounds. Returns a Russian error message or null.
    /// </summary>
    public static string? ValidateImported(string json)
    {
        try
        {
            if (JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
                    { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not JsonObject root)
                return "Файл не является конфигурацией sing-box.";
            if (root["outbounds"] is not JsonArray { Count: > 0 }) return "В конфигурации нет outbounds.";
            if (root["inbounds"] is not JsonArray inbounds || !inbounds.OfType<JsonObject>().Any(inbound =>
                    inbound["type"] is JsonValue type && type.TryGetValue<string>(out var value) && value == "tun"))
                return "В конфигурации нет TUN-входа (inbound типа tun).";
            return null;
        }
        catch (JsonException) { return "Файл не является корректным JSON."; }
    }
}
