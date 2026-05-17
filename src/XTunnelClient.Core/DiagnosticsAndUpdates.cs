using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XTunnelClient.Core;

public sealed class DiagnosticsService
{
    private readonly AppPaths _paths;
    private readonly RuntimeConfigService _configService;
    private readonly SystemProxyService _systemProxy;
    private readonly PortChecker _portChecker;

    public DiagnosticsService(AppPaths paths, RuntimeConfigService configService, SystemProxyService systemProxy, PortChecker portChecker)
    {
        _paths = paths;
        _configService = configService;
        _systemProxy = systemProxy;
        _portChecker = portChecker;
    }

    public async Task<DiagnosticReport> CreateReportAsync(Profile? activeProfile, ControlApiClient? control, CancellationToken cancellationToken = default)
    {
        var report = new DiagnosticReport
        {
            ActiveProfile = activeProfile is null ? null : RedactedProfile(activeProfile),
            CurrentProxy = _systemProxy.Current
        };

        if (activeProfile is not null)
        {
            try
            {
                report.PortChecks = _portChecker.CheckRuntimeConfig(activeProfile.CoreConfigJson, _configService);
            }
            catch (Exception ex)
            {
                report.Warnings.Add($"port check failed: {ex.Message}");
            }
        }

        if (control is not null)
        {
            try { report.Status = await control.GetStatusAsync(cancellationToken); } catch (Exception ex) { report.Warnings.Add($"status failed: {ex.Message}"); }
            try { report.Stats = await control.GetStatsAsync(cancellationToken); } catch (Exception ex) { report.Warnings.Add($"stats failed: {ex.Message}"); }
            try { report.Logs = await control.GetLogsAsync(300, cancellationToken); } catch (Exception ex) { report.Warnings.Add($"logs failed: {ex.Message}"); }
        }

        return report;
    }

    public async Task<string> ExportAsync(DiagnosticReport report, CancellationToken cancellationToken = default)
    {
        _paths.Ensure();
        var path = Path.Combine(_paths.Logs, $"diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        await using var file = new FileStream(path, FileMode.CreateNew);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        await WriteEntryAsync(zip, "report.json", RuntimeConfigService.Redact(JsonSerializer.Serialize(report, JsonDefaults.Pretty)), cancellationToken);

        if (File.Exists(_paths.GuiLog))
        {
            await WriteEntryAsync(zip, "gui.log", RuntimeConfigService.Redact(await File.ReadAllTextAsync(_paths.GuiLog, cancellationToken)), cancellationToken);
        }
        foreach (var log in Directory.EnumerateFiles(_paths.Logs, "core-*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(3))
        {
            await WriteEntryAsync(zip, Path.GetFileName(log), RuntimeConfigService.Redact(await File.ReadAllTextAsync(log, cancellationToken)), cancellationToken);
        }
        return path;
    }

    private static Profile RedactedProfile(Profile profile)
    {
        return new Profile
        {
            Id = profile.Id,
            Name = profile.Name,
            Kind = profile.Kind,
            Enabled = profile.Enabled,
            Source = profile.Source,
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt,
            CoreConfigJson = RuntimeConfigService.Redact(profile.CoreConfigJson),
            SecretRef = profile.SecretRef is null ? null : "secret:redacted",
            Color = profile.Color,
            SortOrder = profile.SortOrder,
            LastValidatedAt = profile.LastValidatedAt,
            LastValidationError = profile.LastValidationError
        };
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string content, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }
}

public sealed class UpdateManifest
{
    public string Version { get; set; } = "";
    public string GuiUrl { get; set; } = "";
    public string CoreUrl { get; set; } = "";
    public string GuiSha256 { get; set; } = "";
    public string CoreSha256 { get; set; } = "";
    public string Signature { get; set; } = "";
}

public sealed class UpdateService
{
    private readonly HttpClient _http;

    public UpdateService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<UpdateManifest> FetchManifestAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetStringAsync(uri, cancellationToken);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonDefaults.Web)
            ?? throw new InvalidOperationException("update manifest 为空");
        ValidateManifest(manifest);
        return manifest;
    }

    public static void ValidateManifest(UpdateManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.GuiUrl) ||
            string.IsNullOrWhiteSpace(manifest.CoreUrl) ||
            string.IsNullOrWhiteSpace(manifest.GuiSha256) ||
            string.IsNullOrWhiteSpace(manifest.CoreSha256))
        {
            throw new InvalidOperationException("update manifest 缺少必要字段");
        }
    }

    public static async Task<bool> VerifySha256Async(string path, string expectedHex, CancellationToken cancellationToken = default)
    {
        await using var file = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(file, cancellationToken);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Equals(actual, expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }
}

public sealed class SubscriptionService
{
    private readonly HttpClient _http;
    private readonly RuntimeConfigService _configService;

    public SubscriptionService(RuntimeConfigService configService, HttpClient? http = null)
    {
        _configService = configService;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<SubscriptionFetchResult> FetchAsync(Subscription subscription, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, subscription.Url);
        if (!string.IsNullOrWhiteSpace(subscription.ETag))
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(subscription.ETag));
        }
        if (DateTimeOffset.TryParse(subscription.LastModified, out var lastModified))
        {
            request.Headers.IfModifiedSince = lastModified;
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            return new SubscriptionFetchResult { NotModified = true, Profiles = [] };
        }
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var profiles = ParseProfiles(payload);
        subscription.ETag = response.Headers.ETag?.Tag;
        subscription.LastModified = response.Content.Headers.LastModified?.ToString();
        subscription.LastUpdatedAt = DateTimeOffset.UtcNow;
        subscription.LastResult = $"fetched {profiles.Count} profile(s)";
        return new SubscriptionFetchResult { Profiles = profiles };
    }

    public List<Profile> ParseProfiles(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var profiles = new List<Profile>();
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in document.RootElement.EnumerateArray())
            {
                profiles.Add(ParseProfileElement(item));
            }
        }
        else
        {
            profiles.Add(ParseProfileElement(document.RootElement));
        }
        return profiles;
    }

    public SubscriptionDiff Diff(IEnumerable<Profile> existing, IEnumerable<Profile> incoming)
    {
        var existingByName = existing.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var added = new List<Profile>();
        var updated = new List<Profile>();
        foreach (var profile in incoming)
        {
            if (!existingByName.TryGetValue(profile.Name, out var current))
            {
                added.Add(profile);
            }
            else if (!string.Equals(Normalize(current.CoreConfigJson), Normalize(profile.CoreConfigJson), StringComparison.Ordinal))
            {
                profile.Id = current.Id;
                profile.CreatedAt = current.CreatedAt;
                profile.SecretRef = current.SecretRef;
                profile.Color = current.Color;
                profile.SortOrder = current.SortOrder;
                profile.Enabled = current.Enabled;
                updated.Add(profile);
            }
        }
        return new SubscriptionDiff(added, updated);
    }

    private Profile ParseProfileElement(JsonElement item)
    {
        if (item.TryGetProperty("core_config", out var coreConfig))
        {
            var profile = new Profile
            {
                Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? "Imported" : "Imported",
                Source = "subscription",
                CoreConfigJson = coreConfig.GetRawText()
            };
            _configService.ParseAndValidate(profile.CoreConfigJson);
            return profile;
        }

        var raw = item.GetRawText();
        _configService.ParseAndValidate(raw);
        return new Profile
        {
            Name = item.TryGetProperty("name", out var plainName) ? plainName.GetString() ?? "Imported" : "Imported",
            Source = "subscription",
            CoreConfigJson = raw
        };
    }

    private static string Normalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, JsonDefaults.Web);
    }
}

public sealed class SubscriptionFetchResult
{
    public bool NotModified { get; set; }
    public List<Profile> Profiles { get; set; } = [];
}

public sealed record SubscriptionDiff(List<Profile> Added, List<Profile> Updated);
