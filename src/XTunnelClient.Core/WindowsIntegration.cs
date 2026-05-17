using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace XTunnelClient.Core;

public interface IProxySettingsStore
{
    ProxySnapshot Read();
    void Write(ProxySnapshot snapshot);
}

public sealed class RegistryProxySettingsStore : IProxySettingsStore
{
    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public ProxySnapshot Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettings, writable: false);
        return new ProxySnapshot
        {
            ProxyEnable = Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0),
            ProxyServer = key?.GetValue("ProxyServer") as string,
            ProxyOverride = key?.GetValue("ProxyOverride") as string,
            AutoConfigUrl = key?.GetValue("AutoConfigURL") as string
        };
    }

    public void Write(ProxySnapshot snapshot)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettings, writable: true);
        key.SetValue("ProxyEnable", snapshot.ProxyEnable, RegistryValueKind.DWord);
        SetOrDelete(key, "ProxyServer", snapshot.ProxyServer);
        SetOrDelete(key, "ProxyOverride", snapshot.ProxyOverride);
        SetOrDelete(key, "AutoConfigURL", snapshot.AutoConfigUrl);
        WinInet.NotifySettingsChanged();
    }

    private static void SetOrDelete(RegistryKey key, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }
        key.SetValue(name, value, RegistryValueKind.String);
    }
}

public sealed class SystemProxyService
{
    private readonly IProxySettingsStore _store;
    private ProxySnapshot? _previous;

    public SystemProxyService(IProxySettingsStore store)
    {
        _store = store;
    }

    public ProxySnapshot Current => _store.Read();

    public void EnableSystemProxy(LocalProxyEndpoints endpoints, string bypassRules)
    {
        _previous ??= _store.Read();
        var proxyParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(endpoints.Http))
        {
            proxyParts.Add($"http={endpoints.Http}");
            proxyParts.Add($"https={endpoints.Http}");
        }
        if (!string.IsNullOrWhiteSpace(endpoints.Socks))
        {
            proxyParts.Add($"socks={endpoints.Socks}");
        }
        if (proxyParts.Count == 0)
        {
            throw new InvalidOperationException("没有可用于 system proxy 的 HTTP/SOCKS5 本地监听");
        }
        _store.Write(new ProxySnapshot
        {
            ProxyEnable = 1,
            ProxyServer = string.Join(';', proxyParts),
            ProxyOverride = bypassRules,
            AutoConfigUrl = null
        });
    }

    public void EnablePac(string pacUrl)
    {
        _previous ??= _store.Read();
        _store.Write(new ProxySnapshot
        {
            ProxyEnable = 0,
            ProxyServer = null,
            ProxyOverride = null,
            AutoConfigUrl = pacUrl
        });
    }

    public void Restore()
    {
        if (_previous is null)
        {
            return;
        }
        _store.Write(_previous);
        _previous = null;
    }
}

public sealed class PacServer : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public string? Url { get; private set; }

    public string Start(LocalProxyEndpoints endpoints, string bypassRules)
    {
        Stop();
        var proxy = endpoints.PreferredHttpOrSocks;
        var port = FreeTcpPort();
        Url = $"http://127.0.0.1:{port}/proxy.pac";
        var prefix = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        _cts = new CancellationTokenSource();
        var pac = BuildPac(proxy, bypassRules);
        _ = Task.Run(() => ServeAsync(_listener, pac, _cts.Token));
        return Url;
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (_listener is not null)
        {
            try { _listener.Stop(); } catch (ObjectDisposedException) { }
            _listener.Close();
        }
        _listener = null;
        _cts = null;
        Url = null;
    }

    public void Dispose() => Stop();

    public static string BuildPac(string proxyAddress, string bypassRules)
    {
        var rules = bypassRules.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tests = rules.Select(rule =>
        {
            if (rule.EndsWith(".*", StringComparison.Ordinal))
            {
                return $"shExpMatch(host, \"{EscapeJs(rule)}\")";
            }
            if (rule.StartsWith("*.", StringComparison.Ordinal))
            {
                return $"dnsDomainIs(host, \"{EscapeJs(rule[1..])}\")";
            }
            return $"host == \"{EscapeJs(rule)}\"";
        });
        return $$"""
            function FindProxyForURL(url, host) {
              if (isPlainHostName(host) || {{string.Join(" || ", tests)}}) {
                return "DIRECT";
              }
              return "PROXY {{proxyAddress}}; SOCKS5 {{proxyAddress}}; DIRECT";
            }
            """;
    }

    private static async Task ServeAsync(HttpListener listener, string pac, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            var data = Encoding.UTF8.GetBytes(pac);
            context.Response.ContentType = "application/x-ns-proxy-autoconfig";
            context.Response.ContentLength64 = data.Length;
            await context.Response.OutputStream.WriteAsync(data, cancellationToken);
            context.Response.Close();
        }
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string EscapeJs(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

public sealed class ProxyCoordinator : IDisposable
{
    private readonly SystemProxyService _systemProxy;
    private readonly PacServer _pacServer;

    public ProxyCoordinator(SystemProxyService systemProxy, PacServer pacServer)
    {
        _systemProxy = systemProxy;
        _pacServer = pacServer;
    }

    public ProxyMode ActiveMode { get; private set; } = ProxyMode.Off;

    public Task ApplyAsync(ProxyMode mode, LocalProxyEndpoints endpoints, AppSettings settings)
    {
        Restore();
        if (mode == ProxyMode.System)
        {
            _systemProxy.EnableSystemProxy(endpoints, settings.PacBypassRules);
            ActiveMode = mode;
        }
        else if (mode == ProxyMode.Pac)
        {
            var pacUrl = _pacServer.Start(endpoints, settings.PacBypassRules);
            _systemProxy.EnablePac(pacUrl);
            ActiveMode = mode;
        }
        else if (mode == ProxyMode.Tun)
        {
            throw new InvalidOperationException("TUN 需要 helper/service，当前客户端已预留入口但不会在普通权限下修改路由");
        }
        return Task.CompletedTask;
    }

    public void Restore()
    {
        _pacServer.Stop();
        _systemProxy.Restore();
        ActiveMode = ProxyMode.Off;
    }

    public void Dispose() => Restore();
}

public sealed class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "x-tunnel-client";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(bool enabled, string executablePath, bool startMinimized)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        var args = startMinimized ? " --minimized" : "";
        key.SetValue(ValueName, $"\"{executablePath}\"{args}", RegistryValueKind.String);
    }
}

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;

    public SingleInstanceGuard(string name)
    {
        _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        IsPrimary = createdNew;
    }

    public bool IsPrimary { get; }

    public void Dispose() => _mutex.Dispose();
}

public static class WindowsAcl
{
    public static void TryRestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }
        try
        {
            var identity = WindowsIdentity.GetCurrent().Name;
            var inheritance = Directory.Exists(path) ? "(OI)(CI)F" : "F";
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "icacls.exe",
                Arguments = $"\"{path}\" /inheritance:r /grant:r \"{identity}:{inheritance}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit(3000);
        }
        catch
        {
            // ACL hardening failure should not make profile editing impossible; diagnostics reports the runtime path.
        }
    }
}

public sealed class PortChecker
{
    public List<PortCheckResult> CheckRuntimeConfig(string runtimeConfigJson, RuntimeConfigService configService)
    {
        var endpoints = configService.GetLocalProxyEndpoints(runtimeConfigJson);
        var results = new List<PortCheckResult>();
        foreach (var address in new[] { endpoints.Http, endpoints.Socks }.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            results.Add(Check(address!));
        }
        return results;
    }

    public PortCheckResult Check(string address)
    {
        try
        {
            if (!Uri.TryCreate("tcp://" + address, UriKind.Absolute, out var uri) || uri.Port <= 0)
            {
                throw new FormatException("地址必须是 host:port");
            }
            var ip = ResolveListenAddress(uri.Host);
            var listener = new TcpListener(ip, uri.Port);
            listener.Start();
            listener.Stop();
            return new PortCheckResult { Address = address, Available = true };
        }
        catch (Exception ex)
        {
            return new PortCheckResult { Address = address, Available = false, Error = ex.Message };
        }
    }

    private static IPAddress ResolveListenAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host == "*" || host == "+")
        {
            return IPAddress.Any;
        }
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }
        return IPAddress.Parse(host);
    }
}

internal static class WinInet
{
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    public static void NotifySettingsChanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
