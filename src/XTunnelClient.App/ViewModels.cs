using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Avalonia.Threading;
using XTunnelClient.Core;

namespace XTunnelClient.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class DashboardMetric
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed class SummaryRow
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed class ProfileIssue
{
    public string Field { get; init; } = "";
    public string Severity { get; init; } = "error";
    public string Message { get; init; } = "";
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppPaths _paths = new();
    private readonly RuntimeConfigService _configService = new();
    private readonly CoreConfigTool _coreConfigTool = new();
    private readonly ProfileRepository _repository;
    private readonly SidecarSupervisor _supervisor;
    private readonly DiagnosticsService _diagnostics;
    private readonly StartupService _startupService = new();
    private readonly CoreLocator _coreLocator;
    private readonly PortChecker _portChecker;

    private Profile? _selectedProfile;
    private AppSettings _settings;
    private RuntimeState _runtimeState = RuntimeState.Stopped;
    private string _statusText = "未连接";
    private string _statsText = "";
    private string _logText = "";
    private string _diagnosticsText = "";
    private string _errorText = "";
    private string _secretValue = "";
    private string _profileListen = "";
    private string _profileForward = "";
    private string _profileMetrics = "";
    private decimal _profileConnections = 1;
    private bool _profileFallback = true;
    private ProxyMode _selectedProxyMode;
    private string _connectionHeading = "Disconnected";
    private string _connectionDetail = "Select a profile and connect the sidecar.";
    private string _activeProfileSummary = "-";
    private string _proxySummary = "Off";
    private string _localProxySummary = "-";
    private string _coreSummary = "Core not running";
    private string _validationSummary = "Not validated";
    private string _recentIssueSummary = "-";
    private string _trayToolTipText = "x-tunnel Client\nDisconnected";

    public MainViewModel()
    {
        _repository = new ProfileRepository(_paths);
        _settings = _repository.GetSettings();
        _selectedProxyMode = _settings.DefaultProxyMode;

        var systemProxy = new SystemProxyService(new RegistryProxySettingsStore());
        var proxyCoordinator = new ProxyCoordinator(systemProxy, new PacServer());
        _portChecker = new PortChecker();
        _coreLocator = new CoreLocator(_paths);
        _supervisor = new SidecarSupervisor(_paths, _repository, _configService, _coreLocator, proxyCoordinator, _portChecker);
        _diagnostics = new DiagnosticsService(_paths, _configService, systemProxy, _portChecker);
        _supervisor.RuntimeChanged += OnRuntimeChanged;

        NewProfileCommand = new RelayCommand(NewProfile);
        DuplicateProfileCommand = new RelayCommand(DuplicateProfile, () => SelectedProfile is not null);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => SelectedProfile is not null);
        SaveProfileCommand = new RelayCommand(SaveSelectedProfile, () => SelectedProfile is not null);
        ApplyFormCommand = new RelayCommand(ApplyStructuredForm, () => SelectedProfile is not null);
        ValidateProfileCommand = new AsyncRelayCommand(ValidateProfileAsync, () => SelectedProfile is not null);
        FormatProfileCommand = new AsyncRelayCommand(FormatProfileAsync, () => SelectedProfile is not null);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        DisconnectCommand = new AsyncRelayCommand(() => _supervisor.DisconnectAsync(), CanDisconnect);
        RestartCommand = new AsyncRelayCommand(RestartAsync, CanRestart);
        RefreshDiagnosticsCommand = new AsyncRelayCommand(RefreshDiagnosticsAsync);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        RestoreProxyCommand = new RelayCommand(() => systemProxy.Restore());
        CopyProxyCommand = new RelayCommand(CopyProxySummary);

        Load();
    }

    public ObservableCollection<Profile> Profiles { get; } = [];
    public ObservableCollection<DashboardMetric> OverviewMetrics { get; } = [];
    public ObservableCollection<SummaryRow> ListenerRows { get; } = [];
    public ObservableCollection<SummaryRow> ChannelRows { get; } = [];
    public ObservableCollection<ProfileIssue> ProfileIssues { get; } = [];
    public IReadOnlyList<ProxyMode> ProxyModes { get; } = Enum.GetValues<ProxyMode>();

    public Profile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                SecretValue = value?.SecretRef is null ? "" : _repository.GetSecret(value.Id, value.SecretRef) ?? "";
                LoadStructuredFields();
                ProfileIssues.Clear();
                RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
                RaiseCommandState();
            }
        }
    }

    public AppSettings Settings
    {
        get => _settings;
        set => SetProperty(ref _settings, value);
    }

    public ProxyMode SelectedProxyMode
    {
        get => _selectedProxyMode;
        set
        {
            if (SetProperty(ref _selectedProxyMode, value))
            {
                Settings.DefaultProxyMode = value;
                RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
            }
        }
    }

    public RuntimeState RuntimeState
    {
        get => _runtimeState;
        set
        {
            if (SetProperty(ref _runtimeState, value))
            {
                RaiseCommandState();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string StatsText
    {
        get => _statsText;
        set => SetProperty(ref _statsText, value);
    }

    public string LogText
    {
        get => _logText;
        set => SetProperty(ref _logText, value);
    }

    public string DiagnosticsText
    {
        get => _diagnosticsText;
        set => SetProperty(ref _diagnosticsText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        set => SetProperty(ref _errorText, value);
    }

    public string SecretValue
    {
        get => _secretValue;
        set => SetProperty(ref _secretValue, value);
    }

    public string ProfileListen
    {
        get => _profileListen;
        set => SetProperty(ref _profileListen, value);
    }

    public string ProfileForward
    {
        get => _profileForward;
        set => SetProperty(ref _profileForward, value);
    }

    public string ProfileMetrics
    {
        get => _profileMetrics;
        set => SetProperty(ref _profileMetrics, value);
    }

    public decimal ProfileConnections
    {
        get => _profileConnections;
        set => SetProperty(ref _profileConnections, value);
    }

    public bool ProfileFallback
    {
        get => _profileFallback;
        set => SetProperty(ref _profileFallback, value);
    }

    public string ConnectionHeading
    {
        get => _connectionHeading;
        set => SetProperty(ref _connectionHeading, value);
    }

    public string ConnectionDetail
    {
        get => _connectionDetail;
        set => SetProperty(ref _connectionDetail, value);
    }

    public string ActiveProfileSummary
    {
        get => _activeProfileSummary;
        set => SetProperty(ref _activeProfileSummary, value);
    }

    public string ProxySummary
    {
        get => _proxySummary;
        set => SetProperty(ref _proxySummary, value);
    }

    public string LocalProxySummary
    {
        get => _localProxySummary;
        set => SetProperty(ref _localProxySummary, value);
    }

    public string CoreSummary
    {
        get => _coreSummary;
        set => SetProperty(ref _coreSummary, value);
    }

    public string ValidationSummary
    {
        get => _validationSummary;
        set => SetProperty(ref _validationSummary, value);
    }

    public string RecentIssueSummary
    {
        get => _recentIssueSummary;
        set => SetProperty(ref _recentIssueSummary, value);
    }

    public string TrayToolTipText
    {
        get => _trayToolTipText;
        set => SetProperty(ref _trayToolTipText, value);
    }

    public ICommand NewProfileCommand { get; }
    public RelayCommand DuplicateProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand SaveProfileCommand { get; }
    public RelayCommand ApplyFormCommand { get; }
    public AsyncRelayCommand ValidateProfileCommand { get; }
    public AsyncRelayCommand FormatProfileCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand RestartCommand { get; }
    public AsyncRelayCommand RefreshDiagnosticsCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand RestoreProxyCommand { get; }
    public ICommand CopyProxyCommand { get; }

    public void Load()
    {
        Profiles.Clear();
        foreach (var profile in _repository.GetProfiles())
        {
            Profiles.Add(profile);
        }
        if (Profiles.Count == 0)
        {
            var profile = new Profile { Name = "Local x-tunnel" };
            _repository.SaveProfile(profile);
            _repository.SaveSecret(profile.Id, profile.SecretRef!, "local-test-token");
            Profiles.Add(profile);
        }
        SelectedProfile ??= Profiles.FirstOrDefault();
        StatusText = "Stopped";
        RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
    }

    public async Task ImportProfileJsonAsync(string json, string name)
    {
        _configService.ParseAndValidate(json);
        var profile = new Profile
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Imported profile" : name,
            CoreConfigJson = json,
            Source = "import"
        };
        _repository.SaveProfile(profile);
        Profiles.Add(profile);
        SelectedProfile = profile;
        await ValidateProfileAsync();
    }

    public string ExportSelectedProfile()
    {
        if (SelectedProfile is null)
        {
            return "";
        }
        var export = new
        {
            SelectedProfile.Id,
            SelectedProfile.Name,
            SelectedProfile.Kind,
            SelectedProfile.Enabled,
            SelectedProfile.Source,
            core_config = JsonSerializer.Deserialize<JsonElement>(RuntimeConfigService.Redact(SelectedProfile.CoreConfigJson), JsonDefaults.Web)
        };
        return JsonSerializer.Serialize(export, JsonDefaults.Pretty);
    }

    public async Task<string> ExportDiagnosticsAsync()
    {
        var report = await _diagnostics.CreateReportAsync(SelectedProfile, _supervisor.Control);
        DiagnosticsText = JsonSerializer.Serialize(report, JsonDefaults.Pretty);
        return await _diagnostics.ExportAsync(report);
    }

    public async Task ShutdownAsync()
    {
        await _supervisor.DisconnectAsync();
    }

    public void Dispose()
    {
        _supervisor.Dispose();
    }

    private void NewProfile()
    {
        var profile = new Profile
        {
            Name = "Profile " + (Profiles.Count + 1),
            SortOrder = Profiles.Count * 100
        };
        Profiles.Add(profile);
        SelectedProfile = profile;
    }

    private void DuplicateProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }
        var copy = new Profile
        {
            Name = SelectedProfile.Name + " Copy",
            Kind = SelectedProfile.Kind,
            Enabled = SelectedProfile.Enabled,
            Source = "local",
            CoreConfigJson = SelectedProfile.CoreConfigJson,
            SecretRef = SelectedProfile.SecretRef,
            Color = SelectedProfile.Color,
            SortOrder = SelectedProfile.SortOrder + 1
        };
        if (!string.IsNullOrWhiteSpace(SecretValue) && copy.SecretRef is not null)
        {
            _repository.SaveSecret(copy.Id, copy.SecretRef, SecretValue);
        }
        _repository.SaveProfile(copy);
        Profiles.Add(copy);
        SelectedProfile = copy;
    }

    private void DeleteProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }
        var profile = SelectedProfile;
        _repository.DeleteProfile(profile.Id);
        Profiles.Remove(profile);
        SelectedProfile = Profiles.FirstOrDefault();
    }

    private void SaveSelectedProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }
        _configService.ParseAndValidate(SelectedProfile.CoreConfigJson);
        _repository.SaveProfile(SelectedProfile);
        if (SelectedProfile.SecretRef is not null)
        {
            _repository.SaveSecret(SelectedProfile.Id, SelectedProfile.SecretRef, SecretValue);
        }
        ErrorText = "Profile saved";
        RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
    }

    private void LoadStructuredFields()
    {
        if (SelectedProfile is null)
        {
            ProfileListen = "";
            ProfileForward = "";
            ProfileMetrics = "";
            ProfileConnections = 1;
            ProfileFallback = true;
            return;
        }

        try
        {
            var obj = _configService.ParseAndValidate(SelectedProfile.CoreConfigJson);
            ProfileListen = obj.TryGetPropertyValue("listen", out var listen) ? listen?.GetValue<string>() ?? "" : "";
            ProfileForward = obj.TryGetPropertyValue("forward", out var forward) ? forward?.GetValue<string>() ?? "" : "";
            ProfileMetrics = obj.TryGetPropertyValue("metrics", out var metrics) ? metrics?.GetValue<string>() ?? "" : "";
            ProfileConnections = obj.TryGetPropertyValue("connections", out var connections) && connections is not null ? connections.GetValue<int>() : 1;
            ProfileFallback = obj.TryGetPropertyValue("fallback", out var fallback) && fallback is not null && fallback.GetValue<bool>();
        }
        catch
        {
            ProfileListen = "";
            ProfileForward = "";
            ProfileMetrics = "";
            ProfileConnections = 1;
            ProfileFallback = true;
        }
    }

    private void ApplyStructuredForm()
    {
        if (SelectedProfile is null)
        {
            return;
        }
        var obj = JsonNode.Parse(SelectedProfile.CoreConfigJson) as JsonObject ?? [];
        obj["listen"] = ProfileListen;
        if (!string.IsNullOrWhiteSpace(ProfileForward))
        {
            obj["forward"] = ProfileForward;
        }
        else
        {
            obj.Remove("forward");
        }
        obj["connections"] = Math.Max(1, (int)ProfileConnections);
        obj["fallback"] = ProfileFallback;
        if (!string.IsNullOrWhiteSpace(ProfileMetrics))
        {
            obj["metrics"] = ProfileMetrics;
        }
        else
        {
            obj.Remove("metrics");
        }
        SelectedProfile.CoreConfigJson = obj.ToJsonString(JsonDefaults.Pretty);
        OnPropertyChanged(nameof(SelectedProfile));
        ErrorText = "Structured fields applied to JSON";
        RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
    }

    private async Task ValidateProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }
        try
        {
            var runtimeJson = GenerateRuntimeConfigForSelectedProfile();
            var corePath = _coreLocator.Resolve(Settings);
            var issues = new List<ProfileIssue>();
            if (corePath is not null)
            {
                await _coreConfigTool.CheckJsonAsync(corePath, runtimeJson);
            }
            else
            {
                _configService.ParseAndValidate(SelectedProfile.CoreConfigJson);
                issues.Add(new ProfileIssue
                {
                    Field = "core",
                    Severity = "warning",
                    Message = "Core path not found; only local JSON validation was run."
                });
            }
            issues.AddRange(CheckProfilePorts(runtimeJson));
            var hasErrors = issues.Any(x => x.Severity == "error");
            var hasWarnings = issues.Any(x => x.Severity == "warning");
            SelectedProfile.LastValidatedAt = DateTimeOffset.UtcNow;
            SelectedProfile.LastValidationError = issues.FirstOrDefault(x => x.Severity == "error")?.Message;
            _repository.SaveProfile(SelectedProfile);
            ReplaceCollection(ProfileIssues, issues.Count == 0
                ? [new ProfileIssue { Field = "profile", Severity = "ok", Message = "Core config check and local port checks passed." }]
                : issues);
            ErrorText = hasErrors
                ? "Profile has blocking validation issues"
                : hasWarnings ? "Profile validated with warnings" : "Profile ready";
        }
        catch (Exception ex)
        {
            var issue = BuildProfileIssue(ex);
            SelectedProfile.LastValidationError = issue.Message;
            ReplaceCollection(ProfileIssues, [issue]);
            ErrorText = issue.Message;
        }
        RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
        await Task.CompletedTask;
    }

    private async Task FormatProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }
        try
        {
            var obj = _configService.ParseAndValidate(SelectedProfile.CoreConfigJson);
            var corePath = _coreLocator.Resolve(Settings);
            if (corePath is not null)
            {
                var formatInput = BuildCoreFormatInput(obj, out var secretField);
                var formatted = await _coreConfigTool.FormatJsonAsync(corePath, formatInput);
                SelectedProfile.CoreConfigJson = RestoreProfileSecretField(formatted, secretField);
                ErrorText = "Formatted with core";
            }
            else
            {
                SelectedProfile.CoreConfigJson = obj.ToJsonString(JsonDefaults.Pretty);
                ErrorText = "Core path not found; formatted locally";
            }
            SelectedProfile.LastValidationError = null;
            OnPropertyChanged(nameof(SelectedProfile));
            LoadStructuredFields();
            ProfileIssues.Clear();
        }
        catch (Exception ex)
        {
            var issue = BuildProfileIssue(ex);
            SelectedProfile.LastValidationError = issue.Message;
            ReplaceCollection(ProfileIssues, [issue]);
            ErrorText = issue.Message;
        }
        RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
    }

    private async Task ConnectAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }
        try
        {
            SaveSelectedProfile();
            SaveSettings();
            await _supervisor.ConnectAsync(SelectedProfile, Settings);
        }
        catch (Exception ex)
        {
            RuntimeState = RuntimeState.Faulted;
            ErrorText = ex.Message;
            StatusText = "Faulted";
        }
    }

    private async Task RestartAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }
        try
        {
            SaveSelectedProfile();
            SaveSettings();
            await _supervisor.RestartAsync(SelectedProfile, Settings);
        }
        catch (Exception ex)
        {
            RuntimeState = RuntimeState.Faulted;
            ErrorText = ex.Message;
            StatusText = "Faulted";
            RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
        }
    }

    private async Task RefreshDiagnosticsAsync()
    {
        var report = await _diagnostics.CreateReportAsync(SelectedProfile, _supervisor.Control);
        DiagnosticsText = JsonSerializer.Serialize(report, JsonDefaults.Pretty);
    }

    private void SaveSettings()
    {
        Settings.DefaultProxyMode = SelectedProxyMode;
        _repository.SaveSettings(Settings);
        var exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
        _startupService.SetEnabled(Settings.LaunchAtLogin, exe, Settings.StartMinimized);
        ErrorText = "Settings saved";
    }

    private void CopyProxySummary()
    {
        if (SelectedProfile is null)
        {
            return;
        }
        try
        {
            var endpoints = _configService.GetLocalProxyEndpoints(SelectedProfile.CoreConfigJson);
            ErrorText = $"HTTP: {endpoints.Http ?? "-"}  SOCKS5: {endpoints.Socks ?? "-"}";
            LocalProxySummary = BuildProxyEndpointSummary(endpoints);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    private void OnRuntimeChanged(object? sender, RuntimeChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RuntimeState = e.State;
            ErrorText = e.Error ?? "";
            StatusText = e.Status is null ? e.State.ToString() : e.Status.ToPrettyJson();
            StatsText = e.Stats?.ToPrettyJson() ?? "";
            LogText = string.Join(Environment.NewLine, _supervisor.Logs.Select(x => $"{x.Time:HH:mm:ss} [{x.Component ?? x.Level}] {x.Message}"));
            RefreshOverview(e.Status, e.Stats);
        });
    }

    private void RefreshOverview(CoreStatus? status, CoreStats? stats)
    {
        ConnectionHeading = RuntimeState switch
        {
            RuntimeState.Running => "Connected",
            RuntimeState.Degraded => "Degraded",
            RuntimeState.Starting => "Starting",
            RuntimeState.Stopping => "Stopping",
            RuntimeState.Faulted => "Faulted",
            RuntimeState.Recovering => "Recovering",
            _ => "Disconnected"
        };
        ConnectionDetail = status is null
            ? "Sidecar is not running."
            : $"{status.Mode} mode, uptime {FormatDuration(status.UptimeSeconds)}";
        ActiveProfileSummary = SelectedProfile is null
            ? "-"
            : $"{SelectedProfile.Name} ({SelectedProfile.Kind}, {SelectedProfile.Source})";
        ProxySummary = SelectedProxyMode.ToString();
        CoreSummary = status is null ? "Core not running" : $"{status.Version} / {ShortCommit(status.Commit)}";
        RecentIssueSummary = FirstNonEmpty(ErrorText, status?.LastFatalError, SelectedProfile?.LastValidationError, "-");
        ValidationSummary = SelectedProfile?.LastValidationError is { Length: > 0 } validationError
            ? validationError
            : SelectedProfile?.LastValidatedAt is { } validatedAt
                ? $"Last checked {validatedAt.LocalDateTime:g}"
                : "Not validated";
        try
        {
            LocalProxySummary = SelectedProfile is null
                ? "-"
                : BuildProxyEndpointSummary(_configService.GetLocalProxyEndpoints(SelectedProfile.CoreConfigJson));
        }
        catch (Exception ex)
        {
            LocalProxySummary = ex.Message;
        }
        TrayToolTipText = $"x-tunnel Client{Environment.NewLine}{ConnectionHeading}{Environment.NewLine}{ActiveProfileSummary}{Environment.NewLine}{LocalProxySummary}";

        var sent = GetJsonUInt64(stats?.Traffic, "bytes_sent");
        var received = GetJsonUInt64(stats?.Traffic, "bytes_received");
        var totalChannels = status?.Client?.Channels.Count ?? 0;
        var upChannels = status?.Client?.Channels.Count(x => x.Up) ?? 0;
        var avgRtt = status?.Client?.Channels.Where(x => x.Up && x.RttSeconds > 0).Select(x => x.RttSeconds).DefaultIfEmpty(0).Average() ?? 0;
        var runningListeners = status?.Listeners.Count(x => string.Equals(x.State, "running", StringComparison.OrdinalIgnoreCase)) ?? 0;
        var totalListeners = status?.Listeners.Count ?? 0;
        var reconnects = GetJsonUInt64(stats?.Counters, "client_reconnects_total");
        var activeStreams = stats?.Server?.ActiveStreams ?? status?.Server?.ActiveStreams ?? 0;

        ReplaceCollection(OverviewMetrics,
        [
            new DashboardMetric { Label = "Runtime", Value = ConnectionHeading, Detail = ConnectionDetail },
            new DashboardMetric { Label = "Traffic", Value = $"{FormatBytes(sent)} up", Detail = $"{FormatBytes(received)} down" },
            new DashboardMetric { Label = "Channels", Value = $"{upChannels}/{totalChannels} up", Detail = avgRtt > 0 ? $"{avgRtt * 1000:0} ms avg RTT" : "waiting for RTT" },
            new DashboardMetric { Label = "Listeners", Value = $"{runningListeners}/{totalListeners} running", Detail = ListenerDetail(status) },
            new DashboardMetric { Label = "Reconnects", Value = reconnects.ToString(CultureInfo.InvariantCulture), Detail = $"{activeStreams} active streams" },
            new DashboardMetric { Label = "Proxy Mode", Value = ProxySummary, Detail = LocalProxySummary }
        ]);

        ReplaceCollection(ListenerRows, status?.Listeners.Select(x => new SummaryRow
        {
            Name = x.Protocol,
            Value = string.IsNullOrWhiteSpace(x.Actual) ? x.Configured : x.Actual,
            Detail = string.IsNullOrWhiteSpace(x.LastError) ? x.State : x.LastError
        }) ?? []);

        ReplaceCollection(ChannelRows, status?.Client?.Channels.Select(x => new SummaryRow
        {
            Name = $"Channel {x.Channel}",
            Value = x.Up ? "Up" : "Down",
            Detail = x.RttSeconds > 0 ? $"{x.RttSeconds * 1000:0} ms RTT, caps {x.Capabilities}" : $"caps {x.Capabilities}"
        }) ?? []);
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private static ulong GetJsonUInt64(JsonElement? element, string propertyName)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetUInt64(out var result) => result,
            _ => 0
        };
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0)
        {
            return "0s";
        }
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }
        if (span.TotalMinutes >= 1)
        {
            return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        }
        return $"{span.Seconds}s";
    }

    private static string BuildProxyEndpointSummary(LocalProxyEndpoints endpoints)
    {
        return $"HTTP {endpoints.Http ?? "-"} / SOCKS {endpoints.Socks ?? "-"}";
    }

    private static string ListenerDetail(CoreStatus? status)
    {
        if (status?.Listeners.Count > 0)
        {
            return string.Join(", ", status.Listeners.Select(x => string.IsNullOrWhiteSpace(x.Actual) ? x.Configured : x.Actual));
        }
        return "no listeners";
    }

    private static string ShortCommit(string? commit)
    {
        if (string.IsNullOrWhiteSpace(commit))
        {
            return "unknown";
        }
        return commit.Length <= 8 ? commit : commit[..8];
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
    }

    private IEnumerable<ProfileIssue> CheckProfilePorts(string runtimeJson)
    {
        foreach (var result in _portChecker.CheckRuntimeConfig(runtimeJson, _configService))
        {
            yield return result.Available
                ? new ProfileIssue
                {
                    Field = "listen",
                    Severity = "ok",
                    Message = $"{result.Address} is available."
                }
                : new ProfileIssue
                {
                    Field = "listen",
                    Severity = "error",
                    Message = $"{result.Address} is occupied or cannot bind: {result.Error}"
                };
        }
    }

    private static ProfileIssue BuildProfileIssue(Exception ex)
    {
        var message = ex.Message.Trim();
        return new ProfileIssue
        {
            Field = InferIssueField(message),
            Severity = "error",
            Message = message
        };
    }

    private static string InferIssueField(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("token") || lower.Contains("secret"))
        {
            return "token";
        }
        if (lower.Contains("listen") || lower.Contains("port") || lower.Contains("bind"))
        {
            return "listen";
        }
        if (lower.Contains("forward") || lower.Contains("websocket"))
        {
            return "forward";
        }
        if (lower.Contains("metrics"))
        {
            return "metrics";
        }
        if (lower.Contains("unknown") || lower.Contains("未知"))
        {
            return "json";
        }
        return "profile";
    }

    private string GenerateRuntimeConfigForSelectedProfile()
    {
        if (SelectedProfile is null)
        {
            return "";
        }
        if (!string.IsNullOrWhiteSpace(SelectedProfile.SecretRef) && !string.IsNullOrEmpty(SecretValue))
        {
            _repository.SaveSecret(SelectedProfile.Id, SelectedProfile.SecretRef, SecretValue);
        }
        return _configService.GenerateRuntimeConfig(SelectedProfile, _repository);
    }

    private static string BuildCoreFormatInput(JsonObject profileConfig, out (string Field, string Value)? secretField)
    {
        secretField = null;
        var obj = profileConfig.DeepClone().AsObject();
        if (obj.TryGetPropertyValue("token_ref", out var tokenRefNode))
        {
            secretField = ("token_ref", tokenRefNode?.GetValue<string>() ?? "");
            obj.Remove("token_ref");
            obj["token"] = "format-placeholder-token";
        }
        else if (obj.TryGetPropertyValue("token", out var tokenNode))
        {
            var token = tokenNode?.GetValue<string>() ?? "";
            if (token.StartsWith("secret:", StringComparison.Ordinal))
            {
                secretField = ("token", token);
                obj["token"] = "format-placeholder-token";
            }
        }
        return obj.ToJsonString(JsonDefaults.Pretty);
    }

    private static string RestoreProfileSecretField(string formattedCoreJson, (string Field, string Value)? secretField)
    {
        var obj = JsonNode.Parse(formattedCoreJson)?.AsObject()
            ?? throw new InvalidOperationException("core format returned non-object JSON");
        if (secretField is { } secret)
        {
            obj.Remove("token");
            obj[secret.Field] = secret.Value;
        }
        return obj.ToJsonString(JsonDefaults.Pretty);
    }

    private bool CanConnect()
    {
        return SelectedProfile is not null && RuntimeState is (RuntimeState.Stopped or RuntimeState.Faulted);
    }

    private bool CanDisconnect()
    {
        return RuntimeState is RuntimeState.Starting or RuntimeState.Running or RuntimeState.Degraded or RuntimeState.Recovering;
    }

    private bool CanRestart()
    {
        return SelectedProfile is not null && RuntimeState is (RuntimeState.Running or RuntimeState.Degraded);
    }

    private void RaiseCommandState()
    {
        DuplicateProfileCommand.RaiseCanExecuteChanged();
        DeleteProfileCommand.RaiseCanExecuteChanged();
        SaveProfileCommand.RaiseCanExecuteChanged();
        ValidateProfileCommand.RaiseCanExecuteChanged();
        FormatProfileCommand.RaiseCanExecuteChanged();
        ApplyFormCommand.RaiseCanExecuteChanged();
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
    }
}
