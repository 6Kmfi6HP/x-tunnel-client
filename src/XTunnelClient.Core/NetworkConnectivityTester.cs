using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

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

    public async Task<NetworkTestResult> TestEndpointAsync(NetworkEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
            timer.Stop();
            return new NetworkTestResult
            {
                Route = "Forward TCP",
                Target = endpoint.Display,
                Success = true,
                DurationMs = (long)timer.Elapsed.TotalMilliseconds
            };
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            timer.Stop();
            return new NetworkTestResult
            {
                Route = "Forward TCP",
                Target = endpoint.Display,
                Success = false,
                DurationMs = (long)timer.Elapsed.TotalMilliseconds,
                Error = ex.Message
            };
        }
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
            var statusCode = (int)response.StatusCode;
            var success = response.IsSuccessStatusCode;
            return new NetworkTestResult
            {
                Route = route,
                Target = target.ToString(),
                Proxy = proxyUri?.ToString(),
                Success = success,
                StatusCode = statusCode,
                DurationMs = (long)timer.Elapsed.TotalMilliseconds,
                Error = success ? null : FormatHttpStatusError(statusCode, response.ReasonPhrase)
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

    private static string FormatHttpStatusError(int statusCode, string? reasonPhrase)
    {
        return string.IsNullOrWhiteSpace(reasonPhrase) ? $"HTTP {statusCode}" : $"HTTP {statusCode} {reasonPhrase}";
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
