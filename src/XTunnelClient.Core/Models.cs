using System.Text.Json;
using System.Text.Json.Serialization;

namespace XTunnelClient.Core;

public enum ProxyMode
{
    Off,
    System,
    Pac,
    Tun
}

public enum RuntimeState
{
    Stopped,
    Starting,
    Running,
    Degraded,
    Stopping,
    Faulted,
    Recovering
}

public sealed class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Profile";
    public string Kind { get; set; } = "client";
    public bool Enabled { get; set; } = true;
    public string Source { get; set; } = "local";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string CoreConfigJson { get; set; } = DefaultClientConfig;
    public string? SecretRef { get; set; } = "profile-token";
    public string? Color { get; set; } = "blue";
    public int SortOrder { get; set; } = 100;
    public DateTimeOffset? LastValidatedAt { get; set; }
    public string? LastValidationError { get; set; }

    public static string DefaultClientConfig =>
        """
        {
          "listen": "socks5://127.0.0.1:10808,http://127.0.0.1:10809",
          "forward": "ws://127.0.0.1:18080/tunnel",
          "token_ref": "secret:profile-token",
          "connections": 1,
          "fallback": true,
          "metrics": "127.0.0.1:0"
        }
        """;
}

public sealed class AppSettings
{
    public string Language { get; set; } = "zh-CN";
    public string Theme { get; set; } = "system";
    public bool LaunchAtLogin { get; set; }
    public bool StartMinimized { get; set; }
    public Guid? AutoConnectProfileId { get; set; }
    public ProxyMode DefaultProxyMode { get; set; } = ProxyMode.Off;
    public string? CorePath { get; set; }
    public string UpdateChannel { get; set; } = "stable";
    public int LogRetentionDays { get; set; } = 14;
    public bool LowPrivacyDiagnostics { get; set; }
    public int StartupDelaySeconds { get; set; } = 8;
    public string PacBypassRules { get; set; } = "localhost;127.*;10.*;192.168.*;*.local";
}

public sealed class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = "Subscription";
    public string Url { get; set; } = "";
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public int UpdateIntervalMinutes { get; set; } = 1440;
    public string LastResult { get; set; } = "never";
    public string TrustPolicy { get; set; } = "confirm";
    public DateTimeOffset? LastUpdatedAt { get; set; }
}

public sealed class ReadyInfo
{
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("commit")]
    public string Commit { get; set; } = "";

    [JsonPropertyName("control_url")]
    public string ControlUrl { get; set; } = "";

    [JsonPropertyName("token_file")]
    public string TokenFile { get; set; } = "";

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }
}

public sealed class CoreVersionInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("commit")]
    public string Commit { get; set; } = "";

    [JsonPropertyName("build")]
    public string Build { get; set; } = "";

    [JsonPropertyName("control_api_version")]
    public int ControlApiVersion { get; set; }

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];
}

public sealed class ControlLogEntry
{
    [JsonPropertyName("id")]
    public ulong Id { get; set; }

    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; set; }

    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    [JsonPropertyName("component")]
    public string? Component { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class ListenerStatus
{
    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "";

    [JsonPropertyName("configured")]
    public string Configured { get; set; } = "";

    [JsonPropertyName("actual")]
    public string Actual { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("last_error")]
    public string? LastError { get; set; }
}

public sealed class ClientChannelStatus
{
    [JsonPropertyName("channel")]
    public int Channel { get; set; }

    [JsonPropertyName("up")]
    public bool Up { get; set; }

    [JsonPropertyName("rtt_seconds")]
    public double RttSeconds { get; set; }

    [JsonPropertyName("capabilities")]
    public ulong Capabilities { get; set; }
}

public sealed class ClientStatus
{
    [JsonPropertyName("forward")]
    public string Forward { get; set; } = "";

    [JsonPropertyName("channels")]
    public List<ClientChannelStatus> Channels { get; set; } = [];

    [JsonPropertyName("fallback")]
    public bool Fallback { get; set; }

    [JsonPropertyName("ech_enabled")]
    public bool EchEnabled { get; set; }

    [JsonPropertyName("front_proxy")]
    public bool FrontProxy { get; set; }
}

public sealed class ServerStatus
{
    [JsonPropertyName("sessions")]
    public int Sessions { get; set; }

    [JsonPropertyName("channels")]
    public int Channels { get; set; }

    [JsonPropertyName("active_streams")]
    public int ActiveStreams { get; set; }
}

public sealed class CoreStatus
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("uptime_seconds")]
    public double UptimeSeconds { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("commit")]
    public string Commit { get; set; } = "";

    [JsonPropertyName("config_hash")]
    public string ConfigHash { get; set; } = "";

    [JsonPropertyName("listeners")]
    public List<ListenerStatus> Listeners { get; set; } = [];

    [JsonPropertyName("client")]
    public ClientStatus? Client { get; set; }

    [JsonPropertyName("server")]
    public ServerStatus? Server { get; set; }

    [JsonPropertyName("last_fatal_error")]
    public string? LastFatalError { get; set; }

    public string ToPrettyJson() => JsonSerializer.Serialize(this, JsonDefaults.Pretty);
}

public sealed class CoreStats
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("traffic")]
    public JsonElement Traffic { get; set; }

    [JsonPropertyName("counters")]
    public JsonElement Counters { get; set; }

    [JsonPropertyName("client")]
    public ClientStatus? Client { get; set; }

    [JsonPropertyName("server")]
    public ServerStatus? Server { get; set; }

    public string ToPrettyJson() => JsonSerializer.Serialize(this, JsonDefaults.Pretty);
}

public sealed class DiagnosticReport
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string GuiVersion { get; set; } = ThisAssemblyVersion;
    public string OsVersion { get; set; } = Environment.OSVersion.VersionString;
    public string Architecture { get; set; } = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
    public string InstallMode { get; set; } = "portable/dev";
    public Profile? ActiveProfile { get; set; }
    public CoreStatus? Status { get; set; }
    public CoreStats? Stats { get; set; }
    public List<ControlLogEntry> Logs { get; set; } = [];
    public ProxySnapshot? CurrentProxy { get; set; }
    public List<PortCheckResult> PortChecks { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    private const string ThisAssemblyVersion = "0.1.0-dev";
}

public sealed class PortCheckResult
{
    public string Address { get; set; } = "";
    public bool Available { get; set; }
    public string? Error { get; set; }
}

public sealed class ProxySnapshot
{
    public int ProxyEnable { get; set; }
    public string? ProxyServer { get; set; }
    public string? ProxyOverride { get; set; }
    public string? AutoConfigUrl { get; set; }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
