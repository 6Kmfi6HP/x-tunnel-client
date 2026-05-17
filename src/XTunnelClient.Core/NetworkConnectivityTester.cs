using System.Diagnostics;
using System.Net;

namespace XTunnelClient.Core;

public sealed class NetworkConnectivityTester
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public async Task<List<NetworkTestResult>> TestAsync(Uri target, LocalProxyEndpoints? endpoints, CancellationToken cancellationToken = default)
    {
        var results = new List<NetworkTestResult>
        {
            await TestHttpAsync("Direct", target, proxyUri: null, cancellationToken)
        };

        var proxyUri = BuildProxyUri(endpoints);
        if (proxyUri is not null)
        {
            results.Add(await TestHttpAsync(FormatProxyRoute(proxyUri), target, proxyUri, cancellationToken));
        }

        return results;
    }

    private static async Task<NetworkTestResult> TestHttpAsync(string route, Uri target, Uri? proxyUri, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                UseProxy = proxyUri is not null,
                Proxy = proxyUri is null ? null : new WebProxy(proxyUri)
            };
            using var client = new HttpClient(handler)
            {
                Timeout = DefaultTimeout
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            timer.Stop();
            return new NetworkTestResult
            {
                Route = route,
                Target = target.ToString(),
                Proxy = proxyUri?.ToString(),
                Success = true,
                StatusCode = (int)response.StatusCode,
                DurationMs = (long)timer.Elapsed.TotalMilliseconds
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or WebException or InvalidOperationException)
        {
            timer.Stop();
            return new NetworkTestResult
            {
                Route = route,
                Target = target.ToString(),
                Proxy = proxyUri?.ToString(),
                Success = false,
                DurationMs = (long)timer.Elapsed.TotalMilliseconds,
                Error = ex.Message
            };
        }
    }

    private static Uri? BuildProxyUri(LocalProxyEndpoints? endpoints)
    {
        if (!string.IsNullOrWhiteSpace(endpoints?.Http))
        {
            return new Uri("http://" + endpoints.Http);
        }
        if (!string.IsNullOrWhiteSpace(endpoints?.Socks))
        {
            return new Uri("socks5://" + endpoints.Socks);
        }
        return null;
    }

    private static string FormatProxyRoute(Uri proxyUri)
    {
        return proxyUri.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase) ? "SOCKS5 proxy" : "HTTP proxy";
    }
}

public sealed class NetworkTestResult
{
    public string Route { get; init; } = "";
    public string Target { get; init; } = "";
    public string? Proxy { get; init; }
    public bool Success { get; init; }
    public int? StatusCode { get; init; }
    public long DurationMs { get; init; }
    public string? Error { get; init; }
}
