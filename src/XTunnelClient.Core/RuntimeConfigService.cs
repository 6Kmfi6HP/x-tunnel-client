using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace XTunnelClient.Core;

public sealed class RuntimeConfigService
{
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "listen",
        "forward",
        "ip",
        "block",
        "cert",
        "key",
        "client_ca",
        "client_cert",
        "client_key",
        "client-ca",
        "client-cert",
        "client-key",
        "token",
        "token_ref",
        "metrics",
        "cidr",
        "allow_target",
        "deny_target",
        "allow_host",
        "deny_host",
        "allow-target",
        "deny-target",
        "allow-host",
        "deny-host",
        "max_clients",
        "max_streams",
        "max-clients",
        "max-streams",
        "dns",
        "ech",
        "ips",
        "connections",
        "insecure",
        "fallback",
        "websocket_front_proxy",
        "dial_timeout",
        "ws_handshake_timeout",
        "reconnect_delay",
        "reconnect_max_delay",
        "reconnect_jitter",
        "rtt_timeout",
        "dns_timeout",
        "ech_retry_delay",
        "udp_read_timeout",
        "shutdown_timeout",
        "auth_skew",
        "preauth_timeout"
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["allow-target"] = "allow_target",
        ["deny-target"] = "deny_target",
        ["allow-host"] = "allow_host",
        ["deny-host"] = "deny_host",
        ["max-clients"] = "max_clients",
        ["max-streams"] = "max_streams",
        ["client-ca"] = "client_ca",
        ["client-cert"] = "client_cert",
        ["client-key"] = "client_key"
    };

    public JsonObject ParseAndValidate(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"配置 JSON 无效: {ex.Message}", ex);
        }

        if (node is not JsonObject obj)
        {
            throw new InvalidOperationException("配置必须是 JSON object");
        }

        foreach (var key in obj.Select(p => p.Key).ToList())
        {
            if (!AllowedFields.Contains(key))
            {
                throw new InvalidOperationException($"未知配置字段: {key}");
            }
        }

        NormalizeAliases(obj);

        if (!obj.TryGetPropertyValue("listen", out var listenNode) || string.IsNullOrWhiteSpace(listenNode?.GetValue<string>()))
        {
            throw new InvalidOperationException("配置必须包含 listen");
        }

        return obj;
    }

    public string GenerateRuntimeConfig(Profile profile, ISecretStore secrets)
    {
        var obj = ParseAndValidate(profile.CoreConfigJson);
        if (obj.TryGetPropertyValue("token_ref", out var tokenRefNode))
        {
            var tokenRef = tokenRefNode?.GetValue<string>() ?? "";
            var secretName = ParseSecretRef(tokenRef);
            var secret = secrets.GetSecret(profile.Id, secretName);
            if (string.IsNullOrEmpty(secret))
            {
                throw new InvalidOperationException($"profile secret 不存在: {secretName}");
            }
            obj.Remove("token_ref");
            obj["token"] = secret;
        }
        else if (obj.TryGetPropertyValue("token", out var tokenNode))
        {
            var token = tokenNode?.GetValue<string>() ?? "";
            if (token.StartsWith("secret:", StringComparison.Ordinal))
            {
                var secretName = ParseSecretRef(token);
                var secret = secrets.GetSecret(profile.Id, secretName);
                if (string.IsNullOrEmpty(secret))
                {
                    throw new InvalidOperationException($"profile secret 不存在: {secretName}");
                }
                obj["token"] = secret;
            }
        }

        return obj.ToJsonString(JsonDefaults.Pretty);
    }

    public async Task<string> WriteRuntimeConfigAsync(Profile profile, ISecretStore secrets, AppPaths paths, CancellationToken cancellationToken)
    {
        paths.Ensure();
        WindowsAcl.TryRestrictToCurrentUser(paths.Runtime);
        var runtimeJson = GenerateRuntimeConfig(profile, secrets);
        await File.WriteAllTextAsync(paths.RuntimeConfig, runtimeJson, new UTF8Encoding(false), cancellationToken);
        WindowsAcl.TryRestrictToCurrentUser(paths.RuntimeConfig);
        return paths.RuntimeConfig;
    }

    public LocalProxyEndpoints GetLocalProxyEndpoints(string runtimeConfigJson)
    {
        var obj = ParseAndValidate(runtimeConfigJson);
        var listen = obj.TryGetPropertyValue("listen", out var node) ? node?.GetValue<string>() ?? "" : "";
        var endpoints = new LocalProxyEndpoints();
        foreach (var item in listen.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(item, UriKind.Absolute, out var uri))
            {
                continue;
            }
            var address = $"{uri.Host}:{uri.Port}";
            if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                endpoints.Http = address;
            }
            else if (uri.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase))
            {
                endpoints.Socks = address;
            }
        }
        return endpoints;
    }

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var redacted = Regex.Replace(text, "(?i)(\"(?:token|password|private_key|control_token)\"\\s*:\\s*\")([^\"]+)(\")", "$1redacted$3");
        redacted = Regex.Replace(redacted, "(?i)(secret:)[^\\s\",}]+", "$1redacted");
        redacted = Regex.Replace(redacted, "([a-z][a-z0-9+.-]*://)([^:/@\\s]+):([^@/\\s]+)@", "$1redacted:redacted@");
        return redacted;
    }

    private static string ParseSecretRef(string tokenRef)
    {
        if (!tokenRef.StartsWith("secret:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"secret reference 必须以 secret: 开头: {tokenRef}");
        }
        var name = tokenRef["secret:".Length..].Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("secret reference 不能为空");
        }
        return name;
    }

    private static void NormalizeAliases(JsonObject obj)
    {
        foreach (var (alias, canonical) in Aliases)
        {
            if (!obj.TryGetPropertyValue(alias, out var value))
            {
                continue;
            }
            if (obj.ContainsKey(canonical))
            {
                throw new InvalidOperationException($"配置字段 {canonical} 和 {alias} 不能同时设置");
            }
            obj.Remove(alias);
            obj[canonical] = value?.DeepClone();
        }
    }
}

public sealed class LocalProxyEndpoints
{
    public string? Http { get; set; }
    public string? Socks { get; set; }

    public string PreferredHttpOrSocks => Http ?? Socks ?? throw new InvalidOperationException("profile 没有 HTTP 或 SOCKS5 本地监听");
}
