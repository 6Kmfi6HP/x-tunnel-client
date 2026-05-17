using System.Diagnostics;
using System.Text.Json;

namespace XTunnelClient.Core;

public sealed class SidecarSupervisor : IDisposable
{
    private readonly AppPaths _paths;
    private readonly ProfileRepository _repository;
    private readonly RuntimeConfigService _configService;
    private readonly CoreConfigTool _configTool = new();
    private readonly CoreLocator _coreLocator;
    private readonly ProxyCoordinator _proxyCoordinator;
    private readonly PortChecker _portChecker;
    private readonly object _gate = new();

    private Process? _process;
    private ControlApiClient? _control;
    private CancellationTokenSource? _monitorCts;
    private string? _trackedCorePath;

    public SidecarSupervisor(
        AppPaths paths,
        ProfileRepository repository,
        RuntimeConfigService configService,
        CoreLocator coreLocator,
        ProxyCoordinator proxyCoordinator,
        PortChecker portChecker)
    {
        _paths = paths;
        _repository = repository;
        _configService = configService;
        _coreLocator = coreLocator;
        _proxyCoordinator = proxyCoordinator;
        _portChecker = portChecker;
    }

    public event EventHandler<RuntimeChangedEventArgs>? RuntimeChanged;

    public RuntimeState State { get; private set; } = RuntimeState.Stopped;
    public string? LastError { get; private set; }
    public CoreStatus? CurrentStatus { get; private set; }
    public CoreStats? CurrentStats { get; private set; }
    public List<ControlLogEntry> Logs { get; } = [];
    public ControlApiClient? Control => _control;

    public async Task ConnectAsync(Profile profile, AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (State is RuntimeState.Running or RuntimeState.Starting or RuntimeState.Degraded)
        {
            await DisconnectAsync(CancellationToken.None);
        }

        SetState(RuntimeState.Starting, null);
        _paths.Ensure();
        CleanupRuntimeFiles();

        var corePath = _coreLocator.Resolve(settings)
            ?? throw new FileNotFoundException("找不到 x-tunnel.exe。请在 Settings 指定 core 路径，或先构建 Go core 到 x-tunnel\\build\\x-tunnel.exe。");
        _trackedCorePath = Path.GetFullPath(corePath);

        var runtimePath = await _configService.WriteRuntimeConfigAsync(profile, _repository, _paths, cancellationToken);
        var runtimeJson = await File.ReadAllTextAsync(runtimePath, cancellationToken);
        var occupied = _portChecker.CheckRuntimeConfig(runtimeJson, _configService).Where(x => !x.Available).ToList();
        if (occupied.Count > 0)
        {
            throw new InvalidOperationException("本地端口占用: " + string.Join(", ", occupied.Select(x => $"{x.Address} {x.Error}")));
        }

        await _configTool.CheckFileAsync(corePath, runtimePath, cancellationToken);

        var coreLogPath = _paths.CoreLogPath();
        _process = StartCore(corePath, runtimePath, coreLogPath);
        _ = PipeProcessOutputAsync(_process, coreLogPath, cancellationToken);

        var ready = await WaitReadyAsync(_paths.ReadyFile, TimeSpan.FromSeconds(10), cancellationToken);
        var token = (await File.ReadAllTextAsync(_paths.TokenFile, cancellationToken)).Trim();
        _control = new ControlApiClient(ready.ControlUrl, token);

        var version = await _control.GetVersionAsync(cancellationToken);
        EnsureCompatible(version);
        await _control.HealthAsync(cancellationToken);
        CurrentStatus = await _control.GetStatusAsync(cancellationToken);
        CurrentStats = await _control.GetStatsAsync(cancellationToken);
        Logs.Clear();
        Logs.AddRange(await _control.GetLogsAsync(200, cancellationToken));

        var endpoints = _configService.GetLocalProxyEndpoints(runtimeJson);
        await _proxyCoordinator.ApplyAsync(settings.DefaultProxyMode, endpoints, settings);

        SetState(CurrentStatus.LastFatalError is null ? RuntimeState.Running : RuntimeState.Degraded, null);
        StartMonitorLoop();
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is RuntimeState.Stopped or RuntimeState.Stopping)
        {
            return;
        }

        SetState(RuntimeState.Stopping, null);
        _proxyCoordinator.Restore();
        _monitorCts?.Cancel();

        if (_control is not null)
        {
            try
            {
                var stopTask = _control.StopAsync(cancellationToken);
                if (await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(4), cancellationToken)) == stopTask)
                {
                    await stopTask;
                }
            }
            catch (Exception ex)
            {
                LastError = $"runtime stop failed: {ex.Message}";
            }
        }

        if (_process is not null)
        {
            try
            {
                await _process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            }
            catch (Exception)
            {
                KillTrackedProcess();
                try
                {
                    await _process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch
                {
                    // The process object is disposed below; a later startup uses a fresh sidecar.
                }
            }
        }

        _control?.Dispose();
        _control = null;
        _process?.Dispose();
        _process = null;
        CurrentStatus = null;
        CurrentStats = null;
        CleanupRuntimeFiles();
        SetState(RuntimeState.Stopped, LastError);
    }

    public async Task RestartAsync(Profile profile, AppSettings settings, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync(cancellationToken);
        await ConnectAsync(profile, settings, cancellationToken);
    }

    public void Dispose()
    {
        _monitorCts?.Cancel();
        _control?.Dispose();
        _process?.Dispose();
        _proxyCoordinator.Dispose();
    }

    private Process StartCore(string corePath, string runtimeConfigPath, string coreLogPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(coreLogPath)!);
        var startInfo = new ProcessStartInfo
        {
            FileName = corePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(corePath) ?? AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("-config");
        startInfo.ArgumentList.Add(runtimeConfigPath);
        startInfo.ArgumentList.Add("-control");
        startInfo.ArgumentList.Add("127.0.0.1:0");
        startInfo.ArgumentList.Add("-ready-file");
        startInfo.ArgumentList.Add(_paths.ReadyFile);
        startInfo.ArgumentList.Add("-control-token-file");
        startInfo.ArgumentList.Add(_paths.TokenFile);

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("启动 x-tunnel.exe 失败");
        return process;
    }

    private static async Task PipeProcessOutputAsync(Process process, string logPath, CancellationToken cancellationToken)
    {
        await using var log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
        var stdout = ReadLinesAsync(process.StandardOutput, log, cancellationToken);
        var stderr = ReadLinesAsync(process.StandardError, log, cancellationToken);
        await Task.WhenAll(stdout, stderr);
    }

    private static async Task ReadLinesAsync(StreamReader reader, StreamWriter log, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }
            if (line.Length > 0)
            {
                await log.WriteLineAsync($"{DateTimeOffset.UtcNow:O} {RuntimeConfigService.Redact(line)}");
            }
        }
    }

    private static async Task<ReadyInfo> WaitReadyAsync(string readyPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(readyPath))
            {
                var raw = await File.ReadAllTextAsync(readyPath, cancellationToken);
                var ready = JsonSerializer.Deserialize<ReadyInfo>(raw, JsonDefaults.Web);
                if (ready is not null && !string.IsNullOrWhiteSpace(ready.ControlUrl))
                {
                    return ready;
                }
            }
            await Task.Delay(150, cancellationToken);
        }
        throw new TimeoutException("等待 sidecar ready file 超时");
    }

    private static void EnsureCompatible(CoreVersionInfo version)
    {
        if (version.ControlApiVersion < 1)
        {
            throw new InvalidOperationException($"core control API 版本过低: {version.ControlApiVersion}");
        }
        var required = new[] { "status", "logs", "stats", "config_check", "config_format", "runtime_stop" };
        var missing = required.Where(x => !version.Capabilities.Contains(x, StringComparer.Ordinal)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException("core 缺少 capabilities: " + string.Join(", ", missing));
        }
    }

    private void StartMonitorLoop()
    {
        _monitorCts?.Cancel();
        _monitorCts = new CancellationTokenSource();
        var token = _monitorCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_process is { HasExited: true })
                    {
                        SetState(RuntimeState.Faulted, "sidecar process exited");
                        _proxyCoordinator.Restore();
                        return;
                    }
                    if (_control is not null)
                    {
                        CurrentStatus = await _control.GetStatusAsync(token);
                        CurrentStats = await _control.GetStatsAsync(token);
                        var logs = await _control.GetLogsAsync(200, token);
                        lock (_gate)
                        {
                            Logs.Clear();
                            Logs.AddRange(logs);
                        }
                        SetState(CurrentStatus.LastFatalError is null ? RuntimeState.Running : RuntimeState.Degraded, CurrentStatus.LastFatalError);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    SetState(RuntimeState.Degraded, ex.Message);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }, token);
    }

    private void KillTrackedProcess()
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }
        try
        {
            if (_trackedCorePath is not null && !string.Equals(Path.GetFullPath(_process.MainModule?.FileName ?? ""), _trackedCorePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        catch
        {
            // The Process instance was created by this supervisor, so it is still safe to terminate even if
            // Windows denies MainModule inspection during shutdown.
        }
        _process.Kill(entireProcessTree: true);
    }

    private void CleanupRuntimeFiles()
    {
        foreach (var path in new[] { _paths.ReadyFile, _paths.TokenFile, _paths.RuntimeConfig })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best effort cleanup; diagnostics keeps the runtime directory visible.
            }
        }
    }

    private void SetState(RuntimeState state, string? error)
    {
        State = state;
        LastError = error;
        RuntimeChanged?.Invoke(this, new RuntimeChangedEventArgs(state, error, CurrentStatus, CurrentStats));
    }
}

public sealed class RuntimeChangedEventArgs : EventArgs
{
    public RuntimeChangedEventArgs(RuntimeState state, string? error, CoreStatus? status, CoreStats? stats)
    {
        State = state;
        Error = error;
        Status = status;
        Stats = stats;
    }

    public RuntimeState State { get; }
    public string? Error { get; }
    public CoreStatus? Status { get; }
    public CoreStats? Stats { get; }
}
