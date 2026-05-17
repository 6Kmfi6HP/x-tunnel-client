using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace XTunnelClient.Core;

public sealed class ControlApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public ControlApiClient(string baseUrl, string? bearerToken = null, HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _ownsHttp = httpClient is null;
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
        }
    }

    public async Task<CoreVersionInfo> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync<CoreVersionInfo>("v1/version", cancellationToken);
    }

    public async Task<bool> HealthAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("v1/health", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    public async Task<CoreStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync<CoreStatus>("v1/status", cancellationToken);
    }

    public async Task<CoreStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync<CoreStats>("v1/stats", cancellationToken);
    }

    public async Task<string> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("v1/metrics", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<List<ControlLogEntry>> GetLogsAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"v1/logs?limit={limit}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("entries", out var entries))
        {
            return [];
        }
        return entries.Deserialize<List<ControlLogEntry>>(JsonDefaults.Web) ?? [];
    }

    public async Task CheckConfigAsync(string json, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync("v1/config/check", JsonContent(json), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<string> FormatConfigAsync(string json, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync("v1/config/format", JsonContent(json), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync("v1/runtime/stop", new StringContent("", Encoding.UTF8, "application/json"), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private async Task<T> GetAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(relativeUrl, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonDefaults.Web, cancellationToken)
            ?? throw new ControlApiException("response.empty", "control API 返回空响应");
    }

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var code = $"http.{(int)response.StatusCode}";
        var message = response.ReasonPhrase ?? "control API request failed";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("code", out var c))
                    {
                        code = c.GetString() ?? code;
                    }
                    if (error.TryGetProperty("message", out var m))
                    {
                        message = m.GetString() ?? message;
                    }
                }
                else if (error.ValueKind == JsonValueKind.String)
                {
                    message = error.GetString() ?? message;
                }
            }
        }
        catch (JsonException)
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                message = body;
            }
        }
        throw new ControlApiException(code, message);
    }
}

public sealed class ControlApiException : Exception
{
    public ControlApiException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
