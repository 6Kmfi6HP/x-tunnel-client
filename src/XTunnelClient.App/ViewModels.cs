using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Styling;
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

public sealed record SubscriptionUpdateResult(int Added, int Updated, int Unchanged, bool NotModified, bool Success, string Message);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppPaths _paths = new();
    private readonly RuntimeConfigService _configService = new();
    private readonly CoreConfigTool _coreConfigTool = new();
    private readonly ProfileRepository _repository;
    private readonly SidecarSupervisor _supervisor;
    private readonly DiagnosticsService _diagnostics;
    private readonly SubscriptionService _subscriptionService;
    private readonly NetworkConnectivityTester _networkTester = new();
    private readonly StartupService _startupService = new();
    private readonly CoreLocator _coreLocator;
    private readonly PortChecker _portChecker;

    private Profile? _selectedProfile;
    private Subscription? _selectedSubscription;
    private AppSettings _settings;
    private RuntimeState _runtimeState = RuntimeState.Stopped;
    private string _statusText = "未连接";
    private string _statsText = "";
    private string _logText = "";
    private List<ControlLogEntry> _logEntries = [];
    private string _filteredLogText = "";
    private string _logFilterText = "";
    private string _logFilterSummary = "Showing all logs";
    private string _logFilterBadgeText = "All logs";
    private string _logFilterBadgeBackground = "#374151";
    private string _logFilterBadgeForeground = "#E5E7EB";
    private string _selectedLogLevelFilter = "All";
    private string _profileSearchText = "";
    private string _selectedProfileSort = "Saved";
    private int _selectedMainTabIndex;
    private string _diagnosticsText = "";
    private string _diagnosticsSummaryText = "";
    private string _diagnosticsLastRunText = "Diagnostics not run";
    private string _diagnosticsPortStatus = "Ports: not checked";
    private string _diagnosticsPortDetail = "Run checks to inspect selected profile listen ports.";
    private string _diagnosticsPortBackground = "#374151";
    private string _diagnosticsPortForeground = "#E5E7EB";
    private string _subscriptionStatusText = "";
    private string _subscriptionSummaryText = "";
    private string _subscriptionSearchText = "";
    private string _selectedSubscriptionSort = "Saved";
    private string _networkTestUrl = "https://www.gstatic.com/generate_204";
    private string _selectedNetworkTestTarget = "Google 204";
    private bool _applyingNetworkTestTarget;
    private string _networkTestText = "Not tested";
    private string _networkTestSummary = "Not tested";
    private string _networkTestDetail = "Run Test Network to check direct and proxy routes.";
    private string _networkTestLastRunText = "Network test not run";
    private string _networkTestBadgeBackground = "#374151";
    private string _networkTestBadgeForeground = "#E5E7EB";
    private string _networkDirectRouteStatus = "Direct: not tested";
    private string _networkDirectRouteDetail = "Run Test Network";
    private string _networkDirectRouteBackground = "#374151";
    private string _networkDirectRouteForeground = "#E5E7EB";
    private string _networkProxyRouteStatus = "Proxy: not tested";
    private string _networkProxyRouteDetail = "Uses selected profile local proxy";
    private string _networkProxyRouteBackground = "#374151";
    private string _networkProxyRouteForeground = "#E5E7EB";
    private string _profileEndpointRouteStatus = "Forward TCP: not tested";
    private string _profileEndpointRouteDetail = "Uses selected profile forward endpoint";
    private string _profileEndpointRouteBackground = "#374151";
    private string _profileEndpointRouteForeground = "#E5E7EB";
    private string _profileEndpointTestText = "Not tested";
    private string _profileEndpointTestLastRunText = "Forward test not run";
    private string _profileBatchTestText = "Endpoint tests not run";
    private string _profileSummaryText = "";
    private string _detectedCorePath = "";
    private string _corePathStatus = "Core not checked";
    private string _corePathStatusDetail = "Set or auto-detect x-tunnel.exe.";
    private string _corePathStatusBackground = "#374151";
    private string _corePathStatusForeground = "#E5E7EB";
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
    private string _statusBarTrafficSummary = "0 B up / 0 B down";
    private string _statusBarChannelSummary = "0/0 up, RTT waiting";
    private string _validationSummary = "Not validated";
    private string _recentIssueSummary = "-";
    private string _trayToolTipText = "x-tunnel Client\nDisconnected";

    public MainViewModel()
    {
        _repository = new ProfileRepository(_paths);
        _settings = _repository.GetSettings();
        _selectedProxyMode = _settings.DefaultProxyMode;
        _networkTestUrl = string.IsNullOrWhiteSpace(_settings.NetworkTestUrl)
            ? "https://www.gstatic.com/generate_204"
            : _settings.NetworkTestUrl;
        _selectedNetworkTestTarget = FindNetworkTestTargetForUrl(_networkTestUrl);
        _settings.NetworkTestUrl = _networkTestUrl;
        _settings.NetworkTestTarget = _selectedNetworkTestTarget;
        ApplyTheme(_settings.Theme);

        var systemProxy = new SystemProxyService(new RegistryProxySettingsStore());
        var proxyCoordinator = new ProxyCoordinator(systemProxy, new PacServer());
        _portChecker = new PortChecker();
        _coreLocator = new CoreLocator(_paths);
        _supervisor = new SidecarSupervisor(_paths, _repository, _configService, _coreLocator, proxyCoordinator, _portChecker);
        _diagnostics = new DiagnosticsService(_paths, _configService, systemProxy, _portChecker);
        _subscriptionService = new SubscriptionService(_configService);
        _supervisor.RuntimeChanged += OnRuntimeChanged;

        NewProfileCommand = new RelayCommand(NewProfile);
        DuplicateProfileCommand = new RelayCommand(DuplicateProfile, () => SelectedProfile is not null);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => SelectedProfile is not null);
        SaveProfileCommand = new RelayCommand(SaveSelectedProfile, () => SelectedProfile is not null);
        ApplyFormCommand = new RelayCommand(ApplyStructuredForm, () => SelectedProfile is not null);
        ValidateProfileCommand = new AsyncRelayCommand(ValidateProfileAsync, () => SelectedProfile is not null);
        FormatProfileCommand = new AsyncRelayCommand(FormatProfileAsync, () => SelectedProfile is not null);
        CopyProfileSummaryCommand = new RelayCommand(CopyProfileSummary, () => SelectedProfile is not null);
        CopyProfileConfigCommand = new RelayCommand(CopyProfileConfig, () => SelectedProfile is not null);
        CopyProfileIssuesCommand = new RelayCommand(CopyProfileIssues, () => SelectedProfile is not null);
        UseSelectedProfileAtStartupCommand = new RelayCommand(UseSelectedProfileAtStartup, () => SelectedProfile is not null);
        ClearStartupProfileCommand = new RelayCommand(ClearStartupProfile, () => Settings.AutoConnectProfileId is not null);
        NewSubscriptionCommand = new RelayCommand(NewSubscription);
        SaveSubscriptionCommand = new RelayCommand(SaveSelectedSubscription, () => SelectedSubscription is not null);
        DeleteSubscriptionCommand = new RelayCommand(DeleteSelectedSubscription, () => SelectedSubscription is not null);
        RefreshSubscriptionCommand = new AsyncRelayCommand(RefreshSelectedSubscriptionAsync, () => SelectedSubscription is not null);
        RefreshAllSubscriptionsCommand = new AsyncRelayCommand(RefreshAllSubscriptionsAsync, () => Subscriptions.Count > 0);
        CopySubscriptionStatusCommand = new RelayCommand(CopySubscriptionStatus, HasSubscriptionStatusResult);
        CopySubscriptionSourceCommand = new RelayCommand(CopySubscriptionSource, () => SelectedSubscription is not null);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        DisconnectCommand = new AsyncRelayCommand(() => _supervisor.DisconnectAsync(), CanDisconnect);
        RestartCommand = new AsyncRelayCommand(RestartAsync, CanRestart);
        RefreshDiagnosticsCommand = new AsyncRelayCommand(RefreshDiagnosticsAsync);
        RunAllDiagnosticsCommand = new AsyncRelayCommand(RunAllDiagnosticsAsync);
        OpenProfilesCommand = new RelayCommand(() => SelectedMainTabIndex = 1);
        OpenSubscriptionsCommand = new RelayCommand(() => SelectedMainTabIndex = 2);
        OpenLogsCommand = new RelayCommand(() => SelectedMainTabIndex = 3);
        OpenDiagnosticsCommand = new AsyncRelayCommand(OpenDiagnosticsAsync);
        OpenSettingsCommand = new RelayCommand(() => SelectedMainTabIndex = 5);
        CopyDiagnosticsSummaryCommand = new RelayCommand(CopyDiagnosticsSummary, HasDiagnosticsSummary);
        CopyDiagnosticsReportCommand = new RelayCommand(CopyDiagnosticsReport, HasDiagnosticsReport);
        CopyDiagnosticsPortsCommand = new RelayCommand(CopyDiagnosticsPorts, HasDiagnosticsPortDetail);
        ClearDiagnosticsTestsCommand = new RelayCommand(ClearDiagnosticsTests, HasDiagnosticsTestResults);
        SetProxyModeOffCommand = new RelayCommand(() => SetProxyMode(ProxyMode.Off));
        SetProxyModeSystemCommand = new RelayCommand(() => SetProxyMode(ProxyMode.System));
        SetProxyModePacCommand = new RelayCommand(() => SetProxyMode(ProxyMode.Pac));
        RunOverviewNetworkTestCommand = new AsyncRelayCommand(RunOverviewNetworkTestAsync);
        TestNetworkCommand = new AsyncRelayCommand(TestNetworkAsync);
        CopyNetworkTestResultCommand = new RelayCommand(CopyNetworkTestResult, HasNetworkTestResult);
        ClearNetworkTestCommand = new RelayCommand(ClearNetworkTest, HasNetworkTestResult);
        TestProfileEndpointCommand = new AsyncRelayCommand(TestProfileEndpointAsync, () => SelectedProfile is not null);
        CopyProfileEndpointTestResultCommand = new RelayCommand(CopyProfileEndpointTestResult, HasProfileEndpointTestResult);
        ClearProfileEndpointTestCommand = new RelayCommand(ClearProfileEndpointTest, HasProfileEndpointTestResult);
        TestSelectedProfileEndpointCommand = new AsyncRelayCommand(TestSelectedProfileEndpointAsync, () => SelectedProfile is not null);
        TestVisibleProfilesCommand = new AsyncRelayCommand(TestVisibleProfilesAsync, () => FilteredProfiles.Count > 0);
        TestAndSelectFastestProfileCommand = new AsyncRelayCommand(TestAndSelectFastestProfileAsync, () => FilteredProfiles.Count > 0);
        SelectFastestProfileCommand = new RelayCommand(SelectFastestProfile, HasSuccessfulVisibleEndpointTest);
        ClearVisibleProfileEndpointTestsCommand = new RelayCommand(ClearVisibleProfileEndpointTests, HasVisibleEndpointTestResults);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        UseDetectedCorePathCommand = new RelayCommand(UseDetectedCorePath);
        CopyCorePathCommand = new RelayCommand(CopyCorePath, HasCorePathToCopy);
        CopySettingsFoldersCommand = new RelayCommand(CopySettingsFolders);
        ClearLogFiltersCommand = new RelayCommand(ClearLogFilters);
        CopyFilteredLogsCommand = new RelayCommand(CopyFilteredLogs, HasFilteredLogs);
        ClearProfileSearchCommand = new RelayCommand(ClearProfileSearch);
        ClearSubscriptionSearchCommand = new RelayCommand(ClearSubscriptionSearch);
        RestoreProxyCommand = new RelayCommand(() => systemProxy.Restore());
        CopyProxyCommand = new RelayCommand(CopyProxySummary);
        CopyOverviewStatusCommand = new RelayCommand(CopyOverviewStatus);
        CopyOverviewRecentLogsCommand = new RelayCommand(CopyOverviewRecentLogs, HasOverviewRecentLogs);
        CopyOverviewRuntimeDetailsCommand = new RelayCommand(CopyOverviewRuntimeDetails, HasOverviewRuntimeDetails);
        CopyRuntimeMetricsCommand = new AsyncRelayCommand(CopyRuntimeMetricsAsync, HasRuntimeControl);
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(_paths.Root));
        OpenProfilesFolderCommand = new RelayCommand(() => OpenFolder(_paths.Profiles));
        OpenLogsFolderCommand = new RelayCommand(() => OpenFolder(_paths.Logs));
        OpenRuntimeFolderCommand = new RelayCommand(() => OpenFolder(_paths.Runtime));

        Load();
        _ = AutoConnectOnStartupAsync();
    }

    public event EventHandler<string>? CopyTextRequested;

    public ObservableCollection<Profile> Profiles { get; } = [];
    public ObservableCollection<Profile> FilteredProfiles { get; } = [];
    public ObservableCollection<DashboardMetric> OverviewMetrics { get; } = [];
    public ObservableCollection<SummaryRow> ListenerRows { get; } = [];
    public ObservableCollection<SummaryRow> ChannelRows { get; } = [];
    public ObservableCollection<ProfileIssue> ProfileIssues { get; } = [];
    public ObservableCollection<Subscription> Subscriptions { get; } = [];
    public ObservableCollection<Subscription> FilteredSubscriptions { get; } = [];
    public IReadOnlyList<ProxyMode> ProxyModes { get; } = Enum.GetValues<ProxyMode>();
    public IReadOnlyList<string> ProfileKinds { get; } = ["client", "server"];
    public IReadOnlyList<string> ThemeOptions { get; } = ["system", "light", "dark"];
    public IReadOnlyList<string> UpdateChannels { get; } = ["stable", "beta", "disabled"];
    public IReadOnlyList<string> SubscriptionTrustPolicies { get; } = ["confirm", "auto"];
    public IReadOnlyList<string> LogLevelFilters { get; } = ["All", "debug", "info", "warn", "error"];
    public IReadOnlyList<string> ProfileSortOptions { get; } = ["Saved", "Name", "Endpoint"];
    public IReadOnlyList<string> SubscriptionSortOptions { get; } = ["Saved", "Name", "Updated", "Status"];
    public IReadOnlyList<string> NetworkTestTargets { get; } = ["Google 204", "Microsoft NCSI", "Cloudflare Trace", "Firefox Success", "Custom"];

    public int SelectedMainTabIndex
    {
        get => _selectedMainTabIndex;
        set => SetProperty(ref _selectedMainTabIndex, value);
    }

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
                OnPropertyChanged(nameof(AutoConnectSelectedProfile));
                OnPropertyChanged(nameof(StartupProfileSummary));
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

    public Subscription? SelectedSubscription
    {
        get => _selectedSubscription;
        set
        {
            if (SetProperty(ref _selectedSubscription, value))
            {
                SubscriptionStatusText = value is null ? "No subscription selected" : BuildSubscriptionStatus(value);
                RaiseSubscriptionCommandState();
            }
        }
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
        set
        {
            if (SetProperty(ref _statusText, value))
            {
                CopyOverviewRuntimeDetailsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatsText
    {
        get => _statsText;
        set => SetProperty(ref _statsText, value);
    }

    public string LogText
    {
        get => _logText;
        set
        {
            if (SetProperty(ref _logText, value))
            {
                UpdateFilteredLogText();
                CopyOverviewRecentLogsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FilteredLogText
    {
        get => _filteredLogText;
        set
        {
            if (SetProperty(ref _filteredLogText, value))
            {
                CopyFilteredLogsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LogFilterText
    {
        get => _logFilterText;
        set
        {
            if (SetProperty(ref _logFilterText, value))
            {
                UpdateFilteredLogText();
            }
        }
    }

    public string LogFilterSummary
    {
        get => _logFilterSummary;
        set => SetProperty(ref _logFilterSummary, value);
    }

    public string LogFilterBadgeText
    {
        get => _logFilterBadgeText;
        private set => SetProperty(ref _logFilterBadgeText, value);
    }

    public string LogFilterBadgeBackground
    {
        get => _logFilterBadgeBackground;
        private set => SetProperty(ref _logFilterBadgeBackground, value);
    }

    public string LogFilterBadgeForeground
    {
        get => _logFilterBadgeForeground;
        private set => SetProperty(ref _logFilterBadgeForeground, value);
    }

    public string SelectedLogLevelFilter
    {
        get => _selectedLogLevelFilter;
        set
        {
            if (SetProperty(ref _selectedLogLevelFilter, value))
            {
                UpdateFilteredLogText();
            }
        }
    }

    public string ProfileSearchText
    {
        get => _profileSearchText;
        set
        {
            if (SetProperty(ref _profileSearchText, value))
            {
                UpdateFilteredProfiles();
            }
        }
    }

    public string SelectedProfileSort
    {
        get => _selectedProfileSort;
        set
        {
            if (SetProperty(ref _selectedProfileSort, value))
            {
                UpdateFilteredProfiles();
            }
        }
    }

    public string DiagnosticsText
    {
        get => _diagnosticsText;
        set
        {
            if (SetProperty(ref _diagnosticsText, value))
            {
                CopyDiagnosticsReportCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DiagnosticsSummaryText
    {
        get => _diagnosticsSummaryText;
        set
        {
            if (SetProperty(ref _diagnosticsSummaryText, value))
            {
                CopyDiagnosticsSummaryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DiagnosticsLastRunText
    {
        get => _diagnosticsLastRunText;
        private set => SetProperty(ref _diagnosticsLastRunText, value);
    }

    public string DiagnosticsPortStatus
    {
        get => _diagnosticsPortStatus;
        private set => SetProperty(ref _diagnosticsPortStatus, value);
    }

    public string DiagnosticsPortDetail
    {
        get => _diagnosticsPortDetail;
        private set
        {
            if (SetProperty(ref _diagnosticsPortDetail, value))
            {
                CopyDiagnosticsPortsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DiagnosticsPortBackground
    {
        get => _diagnosticsPortBackground;
        private set => SetProperty(ref _diagnosticsPortBackground, value);
    }

    public string DiagnosticsPortForeground
    {
        get => _diagnosticsPortForeground;
        private set => SetProperty(ref _diagnosticsPortForeground, value);
    }

    public string SubscriptionStatusText
    {
        get => _subscriptionStatusText;
        set
        {
            if (SetProperty(ref _subscriptionStatusText, value))
            {
                CopySubscriptionStatusCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SubscriptionSummaryText
    {
        get => _subscriptionSummaryText;
        set => SetProperty(ref _subscriptionSummaryText, value);
    }

    public string SubscriptionSearchText
    {
        get => _subscriptionSearchText;
        set
        {
            if (SetProperty(ref _subscriptionSearchText, value))
            {
                UpdateFilteredSubscriptions();
            }
        }
    }

    public string SelectedSubscriptionSort
    {
        get => _selectedSubscriptionSort;
        set
        {
            if (SetProperty(ref _selectedSubscriptionSort, value))
            {
                UpdateFilteredSubscriptions();
            }
        }
    }

    public string NetworkTestUrl
    {
        get => _networkTestUrl;
        set
        {
            value ??= "";
            if (!SetProperty(ref _networkTestUrl, value))
            {
                return;
            }

            var trimmed = value.Trim();
            Settings.NetworkTestUrl = trimmed;
            NetworkTestSummary = "Ready to test";
            NetworkTestDetail = trimmed;
            NetworkTestLastRunText = "Target changed; run test";
            SetNetworkRouteReady(trimmed);
            if (!_applyingNetworkTestTarget)
            {
                var target = FindNetworkTestTargetForUrl(value);
                Settings.NetworkTestTarget = target;
                if (!string.Equals(_selectedNetworkTestTarget, target, StringComparison.Ordinal))
                {
                    _selectedNetworkTestTarget = target;
                    OnPropertyChanged(nameof(SelectedNetworkTestTarget));
                }
            }
        }
    }

    public string SelectedNetworkTestTarget
    {
        get => _selectedNetworkTestTarget;
        set
        {
            value ??= "Custom";
            if (!SetProperty(ref _selectedNetworkTestTarget, value))
            {
                return;
            }

            Settings.NetworkTestTarget = value;
            if (TryGetNetworkTestTargetUrl(value, out var url))
            {
                _applyingNetworkTestTarget = true;
                try
                {
                    NetworkTestUrl = url;
                }
                finally
                {
                    _applyingNetworkTestTarget = false;
                }
            }
        }
    }

    public string NetworkTestSummary
    {
        get => _networkTestSummary;
        set
        {
            if (SetProperty(ref _networkTestSummary, value))
            {
                UpdateNetworkTestBadge();
            }
        }
    }

    public string NetworkTestDetail
    {
        get => _networkTestDetail;
        set => SetProperty(ref _networkTestDetail, value);
    }

    public string NetworkTestLastRunText
    {
        get => _networkTestLastRunText;
        private set => SetProperty(ref _networkTestLastRunText, value);
    }

    public string NetworkTestBadgeBackground
    {
        get => _networkTestBadgeBackground;
        private set => SetProperty(ref _networkTestBadgeBackground, value);
    }

    public string NetworkTestBadgeForeground
    {
        get => _networkTestBadgeForeground;
        private set => SetProperty(ref _networkTestBadgeForeground, value);
    }

    public string NetworkDirectRouteStatus
    {
        get => _networkDirectRouteStatus;
        private set => SetProperty(ref _networkDirectRouteStatus, value);
    }

    public string NetworkDirectRouteDetail
    {
        get => _networkDirectRouteDetail;
        private set => SetProperty(ref _networkDirectRouteDetail, value);
    }

    public string NetworkDirectRouteBackground
    {
        get => _networkDirectRouteBackground;
        private set => SetProperty(ref _networkDirectRouteBackground, value);
    }

    public string NetworkDirectRouteForeground
    {
        get => _networkDirectRouteForeground;
        private set => SetProperty(ref _networkDirectRouteForeground, value);
    }

    public string NetworkProxyRouteStatus
    {
        get => _networkProxyRouteStatus;
        private set => SetProperty(ref _networkProxyRouteStatus, value);
    }

    public string NetworkProxyRouteDetail
    {
        get => _networkProxyRouteDetail;
        private set => SetProperty(ref _networkProxyRouteDetail, value);
    }

    public string NetworkProxyRouteBackground
    {
        get => _networkProxyRouteBackground;
        private set => SetProperty(ref _networkProxyRouteBackground, value);
    }

    public string NetworkProxyRouteForeground
    {
        get => _networkProxyRouteForeground;
        private set => SetProperty(ref _networkProxyRouteForeground, value);
    }

    public string ProfileEndpointRouteStatus
    {
        get => _profileEndpointRouteStatus;
        private set => SetProperty(ref _profileEndpointRouteStatus, value);
    }

    public string ProfileEndpointRouteDetail
    {
        get => _profileEndpointRouteDetail;
        private set => SetProperty(ref _profileEndpointRouteDetail, value);
    }

    public string ProfileEndpointRouteBackground
    {
        get => _profileEndpointRouteBackground;
        private set => SetProperty(ref _profileEndpointRouteBackground, value);
    }

    public string ProfileEndpointRouteForeground
    {
        get => _profileEndpointRouteForeground;
        private set => SetProperty(ref _profileEndpointRouteForeground, value);
    }

    public string NetworkTestText
    {
        get => _networkTestText;
        set
        {
            if (SetProperty(ref _networkTestText, value))
            {
                CopyNetworkTestResultCommand.RaiseCanExecuteChanged();
                ClearNetworkTestCommand.RaiseCanExecuteChanged();
                ClearDiagnosticsTestsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ProfileEndpointTestText
    {
        get => _profileEndpointTestText;
        set
        {
            if (SetProperty(ref _profileEndpointTestText, value))
            {
                CopyProfileEndpointTestResultCommand.RaiseCanExecuteChanged();
                ClearProfileEndpointTestCommand.RaiseCanExecuteChanged();
                ClearDiagnosticsTestsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ProfileEndpointTestLastRunText
    {
        get => _profileEndpointTestLastRunText;
        private set => SetProperty(ref _profileEndpointTestLastRunText, value);
    }

    public string ProfileBatchTestText
    {
        get => _profileBatchTestText;
        set => SetProperty(ref _profileBatchTestText, value);
    }

    public string ProfileSummaryText
    {
        get => _profileSummaryText;
        set => SetProperty(ref _profileSummaryText, value);
    }

    public string DetectedCorePath
    {
        get => _detectedCorePath;
        set
        {
            if (SetProperty(ref _detectedCorePath, value))
            {
                UpdateCorePathStatus();
                CopyCorePathCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CorePathStatus
    {
        get => _corePathStatus;
        private set => SetProperty(ref _corePathStatus, value);
    }

    public string CorePathStatusDetail
    {
        get => _corePathStatusDetail;
        private set => SetProperty(ref _corePathStatusDetail, value);
    }

    public string CorePathStatusBackground
    {
        get => _corePathStatusBackground;
        private set => SetProperty(ref _corePathStatusBackground, value);
    }

    public string CorePathStatusForeground
    {
        get => _corePathStatusForeground;
        private set => SetProperty(ref _corePathStatusForeground, value);
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

    public string StatusBarTrafficSummary
    {
        get => _statusBarTrafficSummary;
        set => SetProperty(ref _statusBarTrafficSummary, value);
    }

    public string StatusBarChannelSummary
    {
        get => _statusBarChannelSummary;
        set => SetProperty(ref _statusBarChannelSummary, value);
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

    public string CorePath
    {
        get => Settings.CorePath ?? "";
        set
        {
            if (!string.Equals(Settings.CorePath ?? "", value, StringComparison.Ordinal))
            {
                Settings.CorePath = string.IsNullOrWhiteSpace(value) ? null : value;
                OnPropertyChanged();
                UpdateCorePathStatus();
                CopyCorePathCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TrayToolTipText
    {
        get => _trayToolTipText;
        set => SetProperty(ref _trayToolTipText, value);
    }

    public string SelectedTheme
    {
        get => Settings.Theme;
        set
        {
            if (!string.Equals(Settings.Theme, value, StringComparison.Ordinal))
            {
                Settings.Theme = value;
                ApplyTheme(value);
                OnPropertyChanged();
            }
        }
    }

    public string SelectedUpdateChannel
    {
        get => Settings.UpdateChannel;
        set
        {
            if (!string.Equals(Settings.UpdateChannel, value, StringComparison.Ordinal))
            {
                Settings.UpdateChannel = value;
                OnPropertyChanged();
            }
        }
    }

    public bool AutoConnectSelectedProfile
    {
        get => SelectedProfile is not null && Settings.AutoConnectProfileId == SelectedProfile.Id;
        set
        {
            Settings.AutoConnectProfileId = value ? SelectedProfile?.Id : null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StartupProfileSummary));
            ClearStartupProfileCommand.RaiseCanExecuteChanged();
        }
    }

    public string StartupProfileSummary
    {
        get
        {
            if (Settings.AutoConnectProfileId is null)
            {
                return "Startup: not set";
            }

            var profile = Profiles.FirstOrDefault(x => x.Id == Settings.AutoConnectProfileId.Value);
            return profile is null ? "Startup: saved profile missing" : $"Startup: {profile.Name}";
        }
    }

    public ICommand NewProfileCommand { get; }
    public RelayCommand DuplicateProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand SaveProfileCommand { get; }
    public RelayCommand ApplyFormCommand { get; }
    public AsyncRelayCommand ValidateProfileCommand { get; }
    public AsyncRelayCommand FormatProfileCommand { get; }
    public RelayCommand CopyProfileSummaryCommand { get; }
    public RelayCommand CopyProfileConfigCommand { get; }
    public RelayCommand CopyProfileIssuesCommand { get; }
    public RelayCommand UseSelectedProfileAtStartupCommand { get; }
    public RelayCommand ClearStartupProfileCommand { get; }
    public ICommand NewSubscriptionCommand { get; }
    public RelayCommand SaveSubscriptionCommand { get; }
    public RelayCommand DeleteSubscriptionCommand { get; }
    public AsyncRelayCommand RefreshSubscriptionCommand { get; }
    public AsyncRelayCommand RefreshAllSubscriptionsCommand { get; }
    public RelayCommand CopySubscriptionStatusCommand { get; }
    public RelayCommand CopySubscriptionSourceCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand RestartCommand { get; }
    public AsyncRelayCommand RefreshDiagnosticsCommand { get; }
    public AsyncRelayCommand RunAllDiagnosticsCommand { get; }
    public RelayCommand OpenProfilesCommand { get; }
    public RelayCommand OpenSubscriptionsCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public AsyncRelayCommand OpenDiagnosticsCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand CopyDiagnosticsSummaryCommand { get; }
    public RelayCommand CopyDiagnosticsReportCommand { get; }
    public RelayCommand CopyDiagnosticsPortsCommand { get; }
    public RelayCommand ClearDiagnosticsTestsCommand { get; }
    public RelayCommand SetProxyModeOffCommand { get; }
    public RelayCommand SetProxyModeSystemCommand { get; }
    public RelayCommand SetProxyModePacCommand { get; }
    public AsyncRelayCommand RunOverviewNetworkTestCommand { get; }
    public AsyncRelayCommand TestNetworkCommand { get; }
    public RelayCommand CopyNetworkTestResultCommand { get; }
    public RelayCommand ClearNetworkTestCommand { get; }
    public AsyncRelayCommand TestProfileEndpointCommand { get; }
    public RelayCommand CopyProfileEndpointTestResultCommand { get; }
    public RelayCommand ClearProfileEndpointTestCommand { get; }
    public AsyncRelayCommand TestSelectedProfileEndpointCommand { get; }
    public AsyncRelayCommand TestVisibleProfilesCommand { get; }
    public AsyncRelayCommand TestAndSelectFastestProfileCommand { get; }
    public RelayCommand SelectFastestProfileCommand { get; }
    public RelayCommand ClearVisibleProfileEndpointTestsCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand UseDetectedCorePathCommand { get; }
    public RelayCommand CopyCorePathCommand { get; }
    public ICommand CopySettingsFoldersCommand { get; }
    public ICommand ClearLogFiltersCommand { get; }
    public RelayCommand CopyFilteredLogsCommand { get; }
    public ICommand ClearProfileSearchCommand { get; }
    public ICommand ClearSubscriptionSearchCommand { get; }
    public ICommand RestoreProxyCommand { get; }
    public ICommand CopyProxyCommand { get; }
    public ICommand CopyOverviewStatusCommand { get; }
    public RelayCommand CopyOverviewRecentLogsCommand { get; }
    public RelayCommand CopyOverviewRuntimeDetailsCommand { get; }
    public AsyncRelayCommand CopyRuntimeMetricsCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenProfilesFolderCommand { get; }
    public ICommand OpenLogsFolderCommand { get; }
    public ICommand OpenRuntimeFolderCommand { get; }

    public void Load()
    {
        DetectedCorePath = _coreLocator.Resolve(new AppSettings()) ?? "";
        LoadProfiles(Settings.AutoConnectProfileId);
        LoadSubscriptions();
        StatusText = "Stopped";
        UpdateCorePathStatus();
        RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
    }

    private void LoadProfiles(Guid? preferredProfileId = null)
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
        var targetId = preferredProfileId ?? Settings.AutoConnectProfileId;
        UpdateFilteredProfiles(targetId);
        OnPropertyChanged(nameof(StartupProfileSummary));
    }

    private void RefreshProfileListItem(Profile profile)
    {
        var selectedId = SelectedProfile?.Id;
        var index = Profiles.IndexOf(profile);
        if (index >= 0)
        {
            Profiles[index] = profile;
        }
        UpdateFilteredProfiles(selectedId);
    }

    private void LoadSubscriptions(Guid? preferredSubscriptionId = null)
    {
        var targetId = preferredSubscriptionId ?? SelectedSubscription?.Id;
        Subscriptions.Clear();
        foreach (var subscription in _repository.GetSubscriptions())
        {
            Subscriptions.Add(subscription);
        }
        UpdateFilteredSubscriptions(targetId);
        if (Subscriptions.Count == 0)
        {
            SelectedSubscription = null;
            SubscriptionStatusText = "No subscriptions configured";
            RaiseSubscriptionCommandState();
            return;
        }
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
        UpdateFilteredProfiles(profile.Id);
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

    public string BuildSelectedProfileSummary()
    {
        if (SelectedProfile is null)
        {
            return "";
        }

        var profile = SelectedProfile;
        var summary = new StringBuilder();
        summary.AppendLine($"Profile: {profile.Name}");
        summary.AppendLine($"Kind: {profile.Kind}");
        summary.AppendLine($"Source: {profile.Source}");
        summary.AppendLine($"Validation: {profile.ValidationState} - {profile.ValidationDetail}");
        summary.AppendLine($"Endpoint test: {profile.EndpointTestState} - {profile.EndpointTestDetail}");

        try
        {
            var endpoints = _configService.GetLocalProxyEndpoints(profile.CoreConfigJson);
            summary.AppendLine($"Local proxy: {BuildProxyEndpointSummary(endpoints)}");
        }
        catch (Exception ex)
        {
            summary.AppendLine($"Local proxy: {ex.Message}");
        }

        try
        {
            var endpoint = _configService.GetForwardEndpoint(profile.CoreConfigJson);
            summary.AppendLine($"Forward: {endpoint.Display}");
        }
        catch (Exception ex)
        {
            summary.AppendLine($"Forward: {ex.Message}");
        }

        summary.AppendLine("Config:");
        summary.AppendLine(RuntimeConfigService.Redact(profile.CoreConfigJson));
        return summary.ToString().TrimEnd();
    }

    public string BuildSelectedProfileIssuesReport()
    {
        if (SelectedProfile is null)
        {
            return "";
        }

        var profile = SelectedProfile;
        var summary = new StringBuilder();
        summary.AppendLine($"Profile: {profile.Name}");
        summary.AppendLine($"Kind: {profile.Kind}");
        summary.AppendLine($"Source: {profile.Source}");
        summary.AppendLine($"Validation: {profile.ValidationState} - {profile.ValidationDetail}");
        if (!string.IsNullOrWhiteSpace(profile.LastValidationError))
        {
            summary.AppendLine($"Last error: {profile.LastValidationError}");
        }

        if (ProfileIssues.Count == 0)
        {
            summary.AppendLine("Issues: none");
        }
        else
        {
            summary.AppendLine("Issues:");
            foreach (var issue in ProfileIssues)
            {
                summary.AppendLine($"- {issue.Field} [{issue.Severity}]: {issue.Message}");
            }
        }

        return summary.ToString().TrimEnd();
    }

    public string BuildOverviewStatusSummary()
    {
        var summary = new StringBuilder();
        summary.AppendLine($"Status: {ConnectionHeading}");
        summary.AppendLine($"Detail: {ConnectionDetail}");
        summary.AppendLine($"Profile: {ActiveProfileSummary}");
        summary.AppendLine($"Proxy mode: {ProxySummary}");
        summary.AppendLine($"Local proxy: {LocalProxySummary}");
        summary.AppendLine($"Traffic: {StatusBarTrafficSummary}");
        summary.AppendLine($"Channels: {StatusBarChannelSummary}");
        summary.AppendLine($"Network: {NetworkTestSummary}");
        summary.AppendLine($"Network detail: {NetworkTestDetail}");
        summary.AppendLine($"Network updated: {NetworkTestLastRunText}");
        summary.AppendLine($"Network target: {NetworkTestUrl}");
        summary.AppendLine($"Core: {CoreSummary}");
        summary.AppendLine($"Validation: {ValidationSummary}");
        summary.AppendLine($"Issue: {RecentIssueSummary}");
        return summary.ToString().TrimEnd();
    }

    public string BuildSettingsFoldersSummary()
    {
        var summary = new StringBuilder();
        summary.AppendLine($"App data: {_paths.Root}");
        summary.AppendLine($"Profiles: {_paths.Profiles}");
        summary.AppendLine($"Logs: {_paths.Logs}");
        summary.AppendLine($"Runtime: {_paths.Runtime}");
        summary.AppendLine($"Core: {FirstNonEmpty(CorePath, DetectedCorePath, "-")}");
        return summary.ToString().TrimEnd();
    }

    public async Task<string> ExportDiagnosticsAsync()
    {
        var report = await _diagnostics.CreateReportAsync(SelectedProfile, _supervisor.Control);
        DiagnosticsSummaryText = BuildDiagnosticsSummary(report);
        DiagnosticsText = JsonSerializer.Serialize(report, JsonDefaults.Pretty);
        UpdateDiagnosticsPortStatus(report);
        DiagnosticsLastRunText = $"Checks refreshed {DateTimeOffset.Now.LocalDateTime:T}";
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
        UpdateFilteredProfiles(profile.Id);
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
        UpdateFilteredProfiles(copy.Id);
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
        UpdateFilteredProfiles();
    }

    private void NewSubscription()
    {
        var subscription = new Subscription
        {
            DisplayName = "Subscription " + (Subscriptions.Count + 1),
            Url = "https://",
            LastResult = "not saved"
        };
        Subscriptions.Add(subscription);
        SelectedSubscription = subscription;
        UpdateFilteredSubscriptions(subscription.Id);
        RaiseSubscriptionCommandState();
    }

    private void SaveSelectedSubscription()
    {
        if (SelectedSubscription is null || !ValidateSubscription(SelectedSubscription))
        {
            return;
        }
        _repository.SaveSubscription(SelectedSubscription);
        var id = SelectedSubscription.Id;
        LoadSubscriptions(id);
        SubscriptionStatusText = "Subscription saved";
        ErrorText = "Subscription saved";
    }

    private void DeleteSelectedSubscription()
    {
        if (SelectedSubscription is null)
        {
            return;
        }
        var subscription = SelectedSubscription;
        _repository.DeleteSubscription(subscription.Id);
        Subscriptions.Remove(subscription);
        UpdateFilteredSubscriptions();
        SubscriptionStatusText = "Subscription deleted";
        RaiseSubscriptionCommandState();
    }

    private async Task RefreshSelectedSubscriptionAsync()
    {
        if (SelectedSubscription is null || !ValidateSubscription(SelectedSubscription))
        {
            return;
        }

        var subscription = SelectedSubscription;
        SubscriptionStatusText = "Fetching subscription...";
        var result = await UpdateSubscriptionAsync(subscription);
        LoadProfiles(SelectedProfile?.Id);
        LoadSubscriptions(subscription.Id);
        SubscriptionStatusText = BuildSubscriptionStatus(SelectedSubscription ?? subscription);
        ErrorText = result.Success
            ? result.NotModified ? "" : "Subscription updated"
            : result.Message;
    }

    private async Task RefreshAllSubscriptionsAsync()
    {
        if (Subscriptions.Count == 0)
        {
            SubscriptionStatusText = "No subscriptions configured";
            ErrorText = SubscriptionStatusText;
            return;
        }

        var selectedId = SelectedSubscription?.Id;
        SubscriptionStatusText = $"Updating {Subscriptions.Count} subscription(s)...";
        var total = 0;
        var succeeded = 0;
        var failed = 0;
        var notModified = 0;
        var added = 0;
        var updated = 0;
        var unchanged = 0;
        foreach (var subscription in Subscriptions.ToList())
        {
            total++;
            if (!ValidateSubscription(subscription))
            {
                failed++;
                continue;
            }

            var result = await UpdateSubscriptionAsync(subscription);
            if (!result.Success)
            {
                failed++;
                continue;
            }

            succeeded++;
            if (result.NotModified)
            {
                notModified++;
            }
            added += result.Added;
            updated += result.Updated;
            unchanged += result.Unchanged;
        }

        LoadProfiles(SelectedProfile?.Id);
        LoadSubscriptions(selectedId);
        SubscriptionStatusText = string.Join(Environment.NewLine,
        [
            $"Updated all {total} subscription(s)",
            $"Succeeded: {succeeded}, failed: {failed}",
            $"Added: {added}, updated: {updated}, unchanged: {unchanged}, not modified: {notModified}"
        ]);
        ErrorText = failed == 0 ? "All subscriptions updated" : "Some subscriptions failed";
    }

    private async Task<SubscriptionUpdateResult> UpdateSubscriptionAsync(Subscription subscription)
    {
        try
        {
            _repository.SaveSubscription(subscription);
            var result = await _subscriptionService.FetchAsync(subscription);
            if (result.NotModified)
            {
                subscription.LastResult = "not modified";
                subscription.LastUpdatedAt = DateTimeOffset.UtcNow;
                _repository.SaveSubscription(subscription);
                return new SubscriptionUpdateResult(0, 0, 0, true, true, subscription.LastResult);
            }

            var diff = _subscriptionService.Diff(_repository.GetProfiles(), result.Profiles);
            foreach (var profile in diff.Added.Concat(diff.Updated))
            {
                _repository.SaveProfile(profile);
            }

            var unchanged = Math.Max(0, result.Profiles.Count - diff.Added.Count - diff.Updated.Count);
            subscription.LastResult = $"added {diff.Added.Count}, updated {diff.Updated.Count}, unchanged {unchanged}";
            subscription.LastUpdatedAt = DateTimeOffset.UtcNow;
            _repository.SaveSubscription(subscription);
            return new SubscriptionUpdateResult(diff.Added.Count, diff.Updated.Count, unchanged, false, true, subscription.LastResult);
        }
        catch (Exception ex)
        {
            subscription.LastResult = $"failed: {ex.Message}";
            _repository.SaveSubscription(subscription);
            return new SubscriptionUpdateResult(0, 0, 0, false, false, ex.Message);
        }
    }

    private void SaveSelectedProfile()
    {
        TrySaveSelectedProfile();
    }

    private bool TrySaveSelectedProfile()
    {
        if (SelectedProfile is null)
        {
            return false;
        }
        try
        {
            _configService.ParseAndValidate(SelectedProfile.CoreConfigJson);
            _repository.SaveProfile(SelectedProfile);
            if (SelectedProfile.SecretRef is not null)
            {
                _repository.SaveSecret(SelectedProfile.Id, SelectedProfile.SecretRef, SecretValue);
            }
            SelectedProfile.LastValidationError = null;
            RefreshProfileListItem(SelectedProfile);
            ProfileIssues.Clear();
            ErrorText = "Profile saved";
            RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
            return true;
        }
        catch (Exception ex)
        {
            ReportProfileError(ex);
            RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
            return false;
        }
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
        try
        {
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
            ProfileIssues.Clear();
            ErrorText = "Structured fields applied to JSON";
            RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
        }
        catch (Exception ex)
        {
            ReportProfileError(ex);
            RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
        }
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
            RefreshProfileListItem(SelectedProfile);
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
            RefreshProfileListItem(SelectedProfile);
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
            RefreshProfileListItem(SelectedProfile);
            OnPropertyChanged(nameof(SelectedProfile));
            LoadStructuredFields();
            ProfileIssues.Clear();
        }
        catch (Exception ex)
        {
            var issue = BuildProfileIssue(ex);
            SelectedProfile.LastValidationError = issue.Message;
            RefreshProfileListItem(SelectedProfile);
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
            if (!TrySaveSelectedProfile())
            {
                return;
            }
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
            if (!TrySaveSelectedProfile())
            {
                return;
            }
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
        DiagnosticsSummaryText = BuildDiagnosticsSummary(report);
        DiagnosticsText = JsonSerializer.Serialize(report, JsonDefaults.Pretty);
        UpdateDiagnosticsPortStatus(report);
        DiagnosticsLastRunText = $"Checks refreshed {DateTimeOffset.Now.LocalDateTime:T}";
        ErrorText = "Diagnostics refreshed";
    }

    private async Task OpenDiagnosticsAsync()
    {
        SelectedMainTabIndex = 4;
        await RefreshDiagnosticsAsync();
    }

    private async Task RunAllDiagnosticsAsync()
    {
        SelectedMainTabIndex = 4;
        ErrorText = "Running diagnostics...";
        await RefreshDiagnosticsAsync();
        await TestNetworkAsync();
        await TestProfileEndpointAsync();
        ErrorText = "Diagnostics run complete";
    }

    private void CopyDiagnosticsSummary()
    {
        if (!HasDiagnosticsSummary())
        {
            return;
        }

        CopyTextRequested?.Invoke(this, BuildDiagnosticsShareSummary());
        ErrorText = "Diagnostics summary copied";
    }

    private string BuildDiagnosticsShareSummary()
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(DiagnosticsSummaryText))
        {
            lines.Add(DiagnosticsSummaryText.Trim());
        }

        if (HasNetworkTestResult())
        {
            lines.Add($"Network: {NetworkTestSummary} ({NetworkTestLastRunText})");
            lines.Add($"Network detail: {NetworkTestDetail}");
        }

        if (HasProfileEndpointTestResult())
        {
            lines.Add($"Forward: {ProfileEndpointRouteStatus} ({ProfileEndpointTestLastRunText})");
            lines.Add($"Forward detail: {ProfileEndpointRouteDetail}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void CopyDiagnosticsReport()
    {
        if (!HasDiagnosticsReport())
        {
            return;
        }

        CopyTextRequested?.Invoke(this, DiagnosticsText);
        ErrorText = "Diagnostics report copied";
    }

    private void CopyDiagnosticsPorts()
    {
        if (!HasDiagnosticsPortDetail())
        {
            return;
        }

        CopyTextRequested?.Invoke(this, $"{DiagnosticsPortStatus}{Environment.NewLine}{DiagnosticsPortDetail}");
        ErrorText = "Diagnostics ports copied";
    }

    private async Task RunOverviewNetworkTestAsync()
    {
        SelectedMainTabIndex = 4;
        await TestNetworkAsync();
    }

    private async Task TestNetworkAsync()
    {
        if (!Uri.TryCreate(NetworkTestUrl.Trim(), UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            NetworkTestText = "Target URL must be an absolute http or https URL";
            NetworkTestSummary = "Invalid target";
            NetworkTestDetail = NetworkTestText;
            NetworkTestLastRunText = $"Last attempted {DateTimeOffset.Now.LocalDateTime:T}";
            SetNetworkRouteInvalid(NetworkTestText);
            ErrorText = NetworkTestText;
            return;
        }

        try
        {
            var endpoints = TryGetSelectedLocalProxyEndpoints(out var endpointError);
            NetworkTestText = "Testing network...";
            NetworkTestSummary = "Testing network...";
            NetworkTestDetail = target.ToString();
            NetworkTestLastRunText = $"Testing started {DateTimeOffset.Now.LocalDateTime:T}";
            SetNetworkRouteTesting(target, endpoints, endpointError);
            var results = await _networkTester.TestAsync(target, endpoints);
            NetworkTestText = FormatNetworkTestResults(results, endpointError);
            UpdateNetworkRouteStatus(results, endpointError);
            UpdateNetworkTestSummary(results, endpointError);
            NetworkTestLastRunText = $"Last tested {DateTimeOffset.Now.LocalDateTime:T}";
            ErrorText = results.Any(x => x.Success) ? "" : "Network test failed";
        }
        catch (Exception ex)
        {
            NetworkTestText = $"Network test failed: {ex.Message}";
            NetworkTestSummary = "Network failed";
            NetworkTestDetail = ex.Message;
            NetworkTestLastRunText = $"Last failed {DateTimeOffset.Now.LocalDateTime:T}";
            SetNetworkRouteFailed(ex.Message);
            ErrorText = ex.Message;
        }
    }

    private void CopyNetworkTestResult()
    {
        if (!HasNetworkTestResult())
        {
            return;
        }
        CopyTextRequested?.Invoke(this, NetworkTestText);
        ErrorText = "Network test result copied";
    }

    private void ClearNetworkTest()
    {
        NetworkTestText = "Not tested";
        NetworkTestSummary = "Not tested";
        NetworkTestDetail = "Run Test Network to check direct and proxy routes.";
        NetworkTestLastRunText = "Network test not run";
        SetNetworkRouteNotTested();
        ErrorText = "Network test result cleared";
    }

    private void ClearDiagnosticsTests()
    {
        ClearNetworkTest();
        ClearProfileEndpointTest();
        ErrorText = "Diagnostics test results cleared";
    }

    private void CopyProfileEndpointTestResult()
    {
        if (!HasProfileEndpointTestResult())
        {
            return;
        }
        CopyTextRequested?.Invoke(this, ProfileEndpointTestText);
        ErrorText = "Profile endpoint test result copied";
    }

    private void ClearProfileEndpointTest()
    {
        ProfileEndpointTestText = "Not tested";
        ProfileEndpointTestLastRunText = "Forward test not run";
        SetProfileEndpointRouteNotTested();
        ErrorText = "Profile endpoint test result cleared";
    }

    private async Task TestProfileEndpointAsync()
    {
        if (SelectedProfile is null)
        {
            ProfileEndpointTestText = "No selected profile";
            ProfileEndpointTestLastRunText = $"Last attempted {DateTimeOffset.Now.LocalDateTime:T}";
            SetProfileEndpointRoute("Forward TCP: unavailable", "No selected profile", RouteVisual.Warning);
            return;
        }

        try
        {
            ProfileEndpointTestText = "Testing profile forward endpoint...";
            ProfileEndpointTestLastRunText = $"Testing started {DateTimeOffset.Now.LocalDateTime:T}";
            var endpoint = _configService.GetForwardEndpoint(SelectedProfile.CoreConfigJson);
            SetProfileEndpointRoute("Forward TCP: testing", endpoint.Display, RouteVisual.Busy);
            var result = await _networkTester.TestEndpointAsync(endpoint);
            ProfileEndpointTestText = FormatNetworkTestResults([result], note: null);
            UpdateProfileEndpointRoute(result);
            ProfileEndpointTestLastRunText = $"Last tested {DateTimeOffset.Now.LocalDateTime:T}";
            ErrorText = result.Success ? "" : "Profile endpoint test failed";
        }
        catch (Exception ex)
        {
            ProfileEndpointTestText = $"Profile endpoint test failed: {ex.Message}";
            ProfileEndpointTestLastRunText = $"Last failed {DateTimeOffset.Now.LocalDateTime:T}";
            SetProfileEndpointRoute("Forward TCP: failed", ex.Message, RouteVisual.Failed);
            ErrorText = ex.Message;
        }
    }

    private async Task TestVisibleProfilesAsync()
    {
        var profiles = FilteredProfiles.ToList();
        if (profiles.Count == 0)
        {
            ProfileBatchTestText = "No visible profiles to test";
            return;
        }

        ProfileBatchTestText = $"Testing {profiles.Count} visible profile(s)...";
        var ok = 0;
        var failed = 0;
        foreach (var profile in profiles)
        {
            var result = await TestAndStoreProfileEndpointAsync(profile);
            if (result.Success)
            {
                ok++;
            }
            else
            {
                failed++;
            }
        }

        ProfileBatchTestText = $"Endpoint tests: {ok} ok, {failed} failed";
        ErrorText = failed == 0 ? "" : ProfileBatchTestText;
        SelectFastestProfileCommand.RaiseCanExecuteChanged();
        ClearVisibleProfileEndpointTestsCommand.RaiseCanExecuteChanged();
        RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
    }

    private async Task TestSelectedProfileEndpointAsync()
    {
        if (SelectedProfile is null)
        {
            ProfileBatchTestText = "No selected profile to test";
            ErrorText = ProfileBatchTestText;
            return;
        }

        var profile = SelectedProfile;
        ProfileBatchTestText = $"Testing selected profile: {profile.Name}...";
        var result = await TestAndStoreProfileEndpointAsync(profile);
        if (result.Success)
        {
            ProfileBatchTestText = $"Selected endpoint ok: {profile.Name} ({result.DurationMs}ms)";
            ErrorText = "";
        }
        else
        {
            var error = FirstNonEmpty(result.Error, "Endpoint test failed");
            ProfileBatchTestText = $"Selected endpoint failed: {profile.Name} - {error}";
            ErrorText = ProfileBatchTestText;
        }

        SelectFastestProfileCommand.RaiseCanExecuteChanged();
        ClearVisibleProfileEndpointTestsCommand.RaiseCanExecuteChanged();
        RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
    }

    private async Task<NetworkTestResult> TestAndStoreProfileEndpointAsync(Profile profile)
    {
        try
        {
            var endpoint = _configService.GetForwardEndpoint(profile.CoreConfigJson);
            var result = await _networkTester.TestEndpointAsync(endpoint);
            profile.LastEndpointTestAt = DateTimeOffset.UtcNow;
            profile.LastEndpointTestDurationMs = result.DurationMs;
            profile.LastEndpointTestTarget = result.Target;
            profile.LastEndpointTestError = result.Success ? null : result.Error ?? "Endpoint test failed";
            _repository.SaveProfile(profile);
            RefreshProfileListItem(profile);
            return result;
        }
        catch (Exception ex)
        {
            profile.LastEndpointTestAt = DateTimeOffset.UtcNow;
            profile.LastEndpointTestDurationMs = null;
            profile.LastEndpointTestTarget = null;
            profile.LastEndpointTestError = ex.Message;
            _repository.SaveProfile(profile);
            RefreshProfileListItem(profile);
            return new NetworkTestResult
            {
                Route = "Forward TCP",
                Target = profile.Name,
                Success = false,
                Error = ex.Message
            };
        }
    }

    private async Task TestAndSelectFastestProfileAsync()
    {
        await TestVisibleProfilesAsync();
        SelectFastestProfile();
    }

    private void SelectFastestProfile()
    {
        var fastest = FilteredProfiles
            .Where(HasSuccessfulEndpointTest)
            .OrderBy(x => x.LastEndpointTestDurationMs ?? long.MaxValue)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (fastest is null)
        {
            ProfileBatchTestText = "No successful endpoint test result; run Test Visible first.";
            ErrorText = ProfileBatchTestText;
            return;
        }

        SelectedProfile = fastest;
        ProfileBatchTestText = $"Selected fastest: {fastest.Name} ({fastest.LastEndpointTestDurationMs ?? 0}ms)";
        ErrorText = "";
    }

    private void ClearVisibleProfileEndpointTests()
    {
        var profiles = FilteredProfiles.Where(x => x.LastEndpointTestAt.HasValue).ToList();
        foreach (var profile in profiles)
        {
            profile.LastEndpointTestAt = null;
            profile.LastEndpointTestDurationMs = null;
            profile.LastEndpointTestError = null;
            profile.LastEndpointTestTarget = null;
            _repository.SaveProfile(profile);
            RefreshProfileListItem(profile);
        }

        ProfileBatchTestText = $"Cleared endpoint results for {profiles.Count} visible profile(s)";
        ErrorText = "";
        SelectFastestProfileCommand.RaiseCanExecuteChanged();
        ClearVisibleProfileEndpointTestsCommand.RaiseCanExecuteChanged();
        RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
    }

    private void SaveSettings()
    {
        if (AutoConnectSelectedProfile && SelectedProfile is not null)
        {
            Settings.AutoConnectProfileId = SelectedProfile.Id;
        }
        Settings.DefaultProxyMode = SelectedProxyMode;
        _repository.SaveSettings(Settings);
        ApplyTheme(Settings.Theme);
        var exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
        _startupService.SetEnabled(Settings.LaunchAtLogin, exe, Settings.StartMinimized);
        UpdateCorePathStatus();
        ErrorText = "Settings saved";
    }

    private void SetProxyMode(ProxyMode mode)
    {
        SelectedProxyMode = mode;
        SaveSettings();
        ErrorText = $"Proxy mode: {mode}";
        RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
    }

    private void UseDetectedCorePath()
    {
        DetectedCorePath = _coreLocator.Resolve(new AppSettings()) ?? "";
        if (string.IsNullOrWhiteSpace(DetectedCorePath))
        {
            UpdateCorePathStatus();
            ErrorText = "No x-tunnel.exe was auto-detected";
            return;
        }
        Settings.CorePath = DetectedCorePath;
        OnPropertyChanged(nameof(CorePath));
        UpdateCorePathStatus();
        ErrorText = "Core path set from auto-detect";
    }

    private void CopyCorePath()
    {
        var path = FirstNonEmpty(CorePath, DetectedCorePath).Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        CopyTextRequested?.Invoke(this, path);
        ErrorText = "Core path copied";
    }

    private void CopySettingsFolders()
    {
        CopyTextRequested?.Invoke(this, BuildSettingsFoldersSummary());
        ErrorText = "Settings folders copied";
    }

    private async Task AutoConnectOnStartupAsync()
    {
        if (Settings.AutoConnectProfileId is null)
        {
            return;
        }
        var delay = Math.Clamp(Settings.StartupDelaySeconds, 0, 120);
        if (delay > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(delay));
        }
        if (SelectedProfile?.Id != Settings.AutoConnectProfileId || RuntimeState != RuntimeState.Stopped)
        {
            return;
        }
        await ConnectAsync();
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
            var summary = BuildProxyEndpointSummary(endpoints);
            LocalProxySummary = summary;
            CopyTextRequested?.Invoke(this, summary);
            ErrorText = "Proxy address copied";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    private void CopyOverviewStatus()
    {
        RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
        CopyTextRequested?.Invoke(this, BuildOverviewStatusSummary());
        ErrorText = "Overview status copied";
    }

    private void CopyOverviewRecentLogs()
    {
        if (!HasOverviewRecentLogs())
        {
            return;
        }

        CopyTextRequested?.Invoke(this, LogText);
        ErrorText = "Overview recent logs copied";
    }

    private void CopyOverviewRuntimeDetails()
    {
        if (!HasOverviewRuntimeDetails())
        {
            return;
        }

        CopyTextRequested?.Invoke(this, BuildOverviewRuntimeDetailsShare());
        ErrorText = "Overview runtime details copied";
    }

    private string BuildOverviewRuntimeDetailsShare()
    {
        var summary = new StringBuilder();
        summary.AppendLine("Status:");
        summary.AppendLine(StatusText.Trim());
        if (!string.IsNullOrWhiteSpace(StatsText))
        {
            summary.AppendLine();
            summary.AppendLine("Stats:");
            summary.AppendLine(StatsText.Trim());
        }

        return summary.ToString().TrimEnd();
    }

    private async Task CopyRuntimeMetricsAsync()
    {
        var control = _supervisor.Control;
        if (control is null)
        {
            return;
        }

        try
        {
            var metrics = await control.GetMetricsAsync();
            CopyTextRequested?.Invoke(this, metrics);
            ErrorText = "Runtime metrics copied";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    private void CopyProfileSummary()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        CopyTextRequested?.Invoke(this, BuildSelectedProfileSummary());
        ErrorText = "Profile summary copied";
    }

    private void CopyProfileConfig()
    {
        if (!HasSelectedProfileConfig() || SelectedProfile is null)
        {
            return;
        }

        CopyTextRequested?.Invoke(this, SelectedProfile.CoreConfigJson);
        ErrorText = "Profile config copied";
    }

    private void CopyProfileIssues()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        CopyTextRequested?.Invoke(this, BuildSelectedProfileIssuesReport());
        ErrorText = "Profile issues copied";
    }

    private void UseSelectedProfileAtStartup()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        Settings.AutoConnectProfileId = SelectedProfile.Id;
        SaveSettings();
        NotifyStartupProfileChanged();
        ErrorText = $"Startup profile: {SelectedProfile.Name}";
    }

    private void ClearStartupProfile()
    {
        Settings.AutoConnectProfileId = null;
        SaveSettings();
        NotifyStartupProfileChanged();
        ErrorText = "Startup profile cleared";
    }

    private void NotifyStartupProfileChanged()
    {
        OnPropertyChanged(nameof(AutoConnectSelectedProfile));
        OnPropertyChanged(nameof(StartupProfileSummary));
        ClearStartupProfileCommand.RaiseCanExecuteChanged();
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            ErrorText = "";
        }
        catch (Exception ex)
        {
            ErrorText = $"Open folder failed: {ex.Message}";
        }
    }

    private void ClearLogFilters()
    {
        LogFilterText = "";
        SelectedLogLevelFilter = "All";
        UpdateFilteredLogText();
    }

    private void CopyFilteredLogs()
    {
        if (!HasFilteredLogs())
        {
            return;
        }

        CopyTextRequested?.Invoke(this, FilteredLogText);
        ErrorText = "Filtered logs copied";
    }

    private void CopySubscriptionStatus()
    {
        if (!HasSubscriptionStatusResult())
        {
            return;
        }

        CopyTextRequested?.Invoke(this, SubscriptionStatusText);
        ErrorText = "Subscription result copied";
    }

    private void CopySubscriptionSource()
    {
        if (SelectedSubscription is null)
        {
            return;
        }

        CopyTextRequested?.Invoke(this, BuildSubscriptionSourceSummary(SelectedSubscription));
        ErrorText = "Subscription source copied";
    }

    private void ClearProfileSearch()
    {
        ProfileSearchText = "";
    }

    private void ClearSubscriptionSearch()
    {
        SubscriptionSearchText = "";
    }

    private void OnRuntimeChanged(object? sender, RuntimeChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RuntimeState = e.State;
            ErrorText = e.Error ?? "";
            StatusText = e.Status is null ? e.State.ToString() : e.Status.ToPrettyJson();
            StatsText = e.Stats?.ToPrettyJson() ?? "";
            _logEntries = _supervisor.Logs.ToList();
            LogText = FormatLogs(_supervisor.Logs);
            RefreshOverview(e.Status, e.Stats);
            CopyRuntimeMetricsCommand.RaiseCanExecuteChanged();
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
        StatusBarTrafficSummary = $"{FormatBytes(sent)} up / {FormatBytes(received)} down";
        StatusBarChannelSummary = avgRtt > 0
            ? $"{upChannels}/{totalChannels} up, {avgRtt * 1000:0} ms RTT"
            : $"{upChannels}/{totalChannels} up, RTT waiting";

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

    private void UpdateCorePathStatus()
    {
        var configured = Settings.CorePath?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            SetCorePathStatusForPath(configured, configuredSource: true);
            return;
        }

        var resolved = _coreLocator.Resolve(Settings);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            SetCorePathStatus("Auto-detected", resolved, "#065F46", "#D1FAE5");
            return;
        }

        SetCorePathStatus("Core missing", "Set a full path to x-tunnel.exe or place it in the expected build/output folder.", "#7F1D1D", "#FECACA");
    }

    private void SetCorePathStatusForPath(string path, bool configuredSource)
    {
        if (!File.Exists(path))
        {
            SetCorePathStatus("Path missing", path, "#7F1D1D", "#FECACA");
            return;
        }

        if (!string.Equals(Path.GetFileName(path), "x-tunnel.exe", StringComparison.OrdinalIgnoreCase))
        {
            SetCorePathStatus("Check executable", path, "#92400E", "#FEF3C7");
            return;
        }

        SetCorePathStatus(configuredSource ? "Configured" : "Auto-detected", path, "#065F46", "#D1FAE5");
    }

    private void SetCorePathStatus(string status, string detail, string background, string foreground)
    {
        CorePathStatus = status;
        CorePathStatusDetail = detail;
        CorePathStatusBackground = background;
        CorePathStatusForeground = foreground;
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

    private void UpdateFilteredLogText()
    {
        var filter = LogFilterText.Trim();
        var level = SelectedLogLevelFilter;
        if (!TryBuildLogRegex(filter, out var regex, out var regexError))
        {
            FilteredLogText = "";
            LogFilterSummary = $"Invalid regex: {regexError}";
            SetLogFilterBadge("Invalid regex", "#7F1D1D", "#FECACA");
            return;
        }

        if (_logEntries.Count > 0)
        {
            IEnumerable<ControlLogEntry> entries = _logEntries;
            if (!string.Equals(level, "All", StringComparison.OrdinalIgnoreCase))
            {
                entries = entries.Where(x => string.Equals(x.Level, level, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(filter))
            {
                entries = entries.Where(x => MatchesLogFilter(FormatLogEntry(x), filter, regex));
            }
            var filtered = entries.ToList();
            FilteredLogText = FormatLogs(filtered);
            UpdateLogFilterSummary(filtered.Count, _logEntries.Count, filter, regex);
            return;
        }

        if (string.IsNullOrWhiteSpace(filter) && string.Equals(level, "All", StringComparison.OrdinalIgnoreCase))
        {
            FilteredLogText = LogText;
            var allCount = CountLogLines(LogText);
            UpdateLogFilterSummary(allCount, allCount, filter, regex);
            return;
        }
        var lines = LogText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var total = lines.Length;
        if (!string.Equals(level, "All", StringComparison.OrdinalIgnoreCase))
        {
            lines = lines.Where(x => x.Contains($"[{level}", StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        if (!string.IsNullOrWhiteSpace(filter))
        {
            lines = lines.Where(x => MatchesLogFilter(x, filter, regex)).ToArray();
        }
        FilteredLogText = string.Join(Environment.NewLine, lines);
        UpdateLogFilterSummary(lines.Length, total, filter, regex);
    }

    private static bool TryBuildLogRegex(string filter, out Regex? regex, out string? error)
    {
        regex = null;
        error = null;
        if (!IsRegexFilter(filter))
        {
            return true;
        }

        try
        {
            regex = new Regex(filter[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsRegexFilter(string filter)
    {
        return filter.Length >= 2 && filter[0] == '/' && filter[^1] == '/';
    }

    private static bool MatchesLogFilter(string line, string filter, Regex? regex)
    {
        return regex is null
            ? line.Contains(filter, StringComparison.OrdinalIgnoreCase)
            : regex.IsMatch(line);
    }

    private void UpdateLogFilterSummary(int shown, int total, string filter, Regex? regex)
    {
        if (string.IsNullOrWhiteSpace(filter) && string.Equals(SelectedLogLevelFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            LogFilterSummary = $"Showing {shown} log line(s)";
            SetLogFilterBadge("All logs", "#374151", "#E5E7EB");
            return;
        }

        var mode = regex is null ? "text" : "regex";
        LogFilterSummary = $"Showing {shown}/{total} log line(s), {mode} filter";
        if (shown == 0 && total > 0)
        {
            SetLogFilterBadge("No matches", "#92400E", "#FEF3C7");
            return;
        }

        SetLogFilterBadge(regex is null ? "Text filter" : "Regex filter", "#1E3A8A", "#BFDBFE");
    }

    private void SetLogFilterBadge(string text, string background, string foreground)
    {
        LogFilterBadgeText = text;
        LogFilterBadgeBackground = background;
        LogFilterBadgeForeground = foreground;
    }

    private static int CountLogLines(string text)
    {
        return text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static string FormatLogs(IEnumerable<ControlLogEntry> logs)
    {
        return string.Join(Environment.NewLine, logs.Select(FormatLogEntry));
    }

    private static string FormatLogEntry(ControlLogEntry log)
    {
        var source = string.IsNullOrWhiteSpace(log.Component) ? log.Level : $"{log.Level}/{log.Component}";
        return $"{log.Time:HH:mm:ss} [{source}] {log.Message}";
    }

    private void UpdateFilteredProfiles(Guid? preferredProfileId = null)
    {
        var selectedId = preferredProfileId ?? SelectedProfile?.Id;
        var matches = ApplyProfileSort(Profiles.Where(MatchesProfileSearch)).ToList();
        ReplaceCollection(FilteredProfiles, matches);
        UpdateProfileSummary();
        TestVisibleProfilesCommand.RaiseCanExecuteChanged();
        TestAndSelectFastestProfileCommand.RaiseCanExecuteChanged();
        SelectFastestProfileCommand.RaiseCanExecuteChanged();
        ClearVisibleProfileEndpointTestsCommand.RaiseCanExecuteChanged();

        if (FilteredProfiles.Count == 0)
        {
            SelectedProfile = null;
            return;
        }

        var preferred = selectedId.HasValue
            ? FilteredProfiles.FirstOrDefault(x => x.Id == selectedId.Value)
            : null;
        SelectedProfile = preferred ?? FilteredProfiles.First();
    }

    private bool MatchesProfileSearch(Profile profile)
    {
        var filter = ProfileSearchText.Trim();
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return ProfileFieldContains(profile.Name, filter)
            || ProfileFieldContains(profile.Kind, filter)
            || ProfileFieldContains(profile.Source, filter)
            || ProfileFieldContains(profile.ValidationState, filter)
            || ProfileFieldContains(profile.ValidationDetail, filter)
            || ProfileFieldContains(profile.EndpointTestState, filter)
            || ProfileFieldContains(profile.EndpointTestDetail, filter);
    }

    private static bool ProfileFieldContains(string? value, string filter)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private bool HasSuccessfulVisibleEndpointTest()
    {
        return FilteredProfiles.Any(HasSuccessfulEndpointTest);
    }

    private bool HasVisibleEndpointTestResults()
    {
        return FilteredProfiles.Any(x => x.LastEndpointTestAt.HasValue);
    }

    private void UpdateProfileSummary()
    {
        var ready = Profiles.Count(x => string.Equals(x.ValidationState, "Ready", StringComparison.Ordinal));
        var issues = Profiles.Count(x => string.Equals(x.ValidationState, "Issue", StringComparison.Ordinal));
        var endpointOk = Profiles.Count(HasSuccessfulEndpointTest);
        ProfileSummaryText = $"Profiles {FilteredProfiles.Count}/{Profiles.Count} visible, {ready} ready, {issues} issues, {endpointOk} endpoint ok";
    }

    private static bool HasSuccessfulEndpointTest(Profile profile)
    {
        return profile.LastEndpointTestAt.HasValue
            && string.IsNullOrWhiteSpace(profile.LastEndpointTestError)
            && profile.LastEndpointTestDurationMs.HasValue;
    }

    private IEnumerable<Profile> ApplyProfileSort(IEnumerable<Profile> profiles)
    {
        return SelectedProfileSort switch
        {
            "Name" => profiles.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            "Endpoint" => profiles
                .OrderBy(EndpointSortGroup)
                .ThenBy(x => x.LastEndpointTestDurationMs ?? long.MaxValue)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            _ => profiles
        };
    }

    private static int EndpointSortGroup(Profile profile)
    {
        if (HasSuccessfulEndpointTest(profile))
        {
            return 0;
        }
        return profile.LastEndpointTestAt.HasValue ? 2 : 1;
    }

    private LocalProxyEndpoints? TryGetSelectedLocalProxyEndpoints(out string? error)
    {
        error = null;
        if (SelectedProfile is null)
        {
            error = "No selected profile; proxy route skipped";
            return null;
        }

        try
        {
            return _configService.GetLocalProxyEndpoints(SelectedProfile.CoreConfigJson);
        }
        catch (Exception ex)
        {
            error = $"Profile proxy endpoint parse failed: {ex.Message}";
            return null;
        }
    }

    private enum RouteVisual
    {
        Neutral,
        Busy,
        Ok,
        Warning,
        Failed
    }

    private void SetNetworkRouteNotTested()
    {
        SetDirectRoute("Direct: not tested", "Run Test Network", RouteVisual.Neutral);
        SetProxyRoute("Proxy: not tested", "Uses selected profile local proxy", RouteVisual.Neutral);
    }

    private void SetNetworkRouteReady(string target)
    {
        var detail = string.IsNullOrWhiteSpace(target) ? "Enter an http or https URL" : target;
        SetDirectRoute("Direct: ready", detail, RouteVisual.Neutral);
        SetProxyRoute("Proxy: ready", "Uses selected profile local proxy", RouteVisual.Neutral);
    }

    private void SetNetworkRouteInvalid(string detail)
    {
        SetDirectRoute("Direct: invalid target", detail, RouteVisual.Failed);
        SetProxyRoute("Proxy: invalid target", detail, RouteVisual.Failed);
    }

    private void SetNetworkRouteTesting(Uri target, LocalProxyEndpoints? endpoints, string? endpointError)
    {
        SetDirectRoute("Direct: testing", target.ToString(), RouteVisual.Busy);
        if (endpoints is null)
        {
            SetProxyRoute("Proxy: skipped", FirstNonEmpty(endpointError, "No selected proxy endpoint"), RouteVisual.Warning);
            return;
        }

        SetProxyRoute("Proxy: testing", BuildProxyEndpointSummary(endpoints), RouteVisual.Busy);
    }

    private void SetNetworkRouteFailed(string detail)
    {
        SetDirectRoute("Direct: failed", detail, RouteVisual.Failed);
        SetProxyRoute("Proxy: failed", detail, RouteVisual.Failed);
    }

    private void UpdateNetworkRouteStatus(IEnumerable<NetworkTestResult> results, string? note)
    {
        var tested = results.ToList();
        var direct = tested.FirstOrDefault(x => string.Equals(x.Route, "Direct", StringComparison.OrdinalIgnoreCase));
        var proxy = tested.FirstOrDefault(x => !string.Equals(x.Route, "Direct", StringComparison.OrdinalIgnoreCase));

        if (direct is null)
        {
            SetDirectRoute("Direct: skipped", "No direct route result", RouteVisual.Warning);
        }
        else
        {
            SetDirectRoute(BuildRouteStatus(direct), BuildRouteDetail(direct), direct.Success ? RouteVisual.Ok : RouteVisual.Failed);
        }

        if (proxy is null)
        {
            SetProxyRoute("Proxy: skipped", FirstNonEmpty(note, "No local proxy endpoint available"), RouteVisual.Warning);
            return;
        }

        SetProxyRoute(BuildRouteStatus(proxy), BuildRouteDetail(proxy), proxy.Success ? RouteVisual.Ok : RouteVisual.Failed);
    }

    private void UpdateProfileEndpointRoute(NetworkTestResult result)
    {
        SetProfileEndpointRoute(BuildRouteStatus(result), BuildRouteDetail(result), result.Success ? RouteVisual.Ok : RouteVisual.Failed);
    }

    private void SetProfileEndpointRouteNotTested()
    {
        SetProfileEndpointRoute("Forward TCP: not tested", "Uses selected profile forward endpoint", RouteVisual.Neutral);
    }

    private void SetDirectRoute(string status, string detail, RouteVisual visual)
    {
        NetworkDirectRouteStatus = status;
        NetworkDirectRouteDetail = detail;
        (NetworkDirectRouteBackground, NetworkDirectRouteForeground) = RouteColors(visual);
    }

    private void SetProxyRoute(string status, string detail, RouteVisual visual)
    {
        NetworkProxyRouteStatus = status;
        NetworkProxyRouteDetail = detail;
        (NetworkProxyRouteBackground, NetworkProxyRouteForeground) = RouteColors(visual);
    }

    private void SetProfileEndpointRoute(string status, string detail, RouteVisual visual)
    {
        ProfileEndpointRouteStatus = status;
        ProfileEndpointRouteDetail = detail;
        (ProfileEndpointRouteBackground, ProfileEndpointRouteForeground) = RouteColors(visual);
    }

    private static string BuildRouteStatus(NetworkTestResult result)
    {
        var status = result.Success ? "ok" : "failed";
        return $"{result.Route}: {status} {result.DurationMs}ms";
    }

    private static string BuildRouteDetail(NetworkTestResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            return result.Error;
        }

        var status = result.StatusCode.HasValue ? $"status {result.StatusCode}; " : "";
        var proxy = string.IsNullOrWhiteSpace(result.Proxy) ? "" : $" via {result.Proxy}";
        return $"{status}{result.Target}{proxy}";
    }

    private static (string Background, string Foreground) RouteColors(RouteVisual visual)
    {
        return visual switch
        {
            RouteVisual.Busy => ("#1E3A8A", "#BFDBFE"),
            RouteVisual.Ok => ("#065F46", "#D1FAE5"),
            RouteVisual.Warning => ("#92400E", "#FEF3C7"),
            RouteVisual.Failed => ("#7F1D1D", "#FECACA"),
            _ => ("#374151", "#E5E7EB")
        };
    }

    private static string FormatNetworkTestResults(IEnumerable<NetworkTestResult> results, string? note)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(note))
        {
            lines.Add(note);
        }
        foreach (var result in results)
        {
            var status = result.Success ? "ok" : "failed";
            var code = result.StatusCode.HasValue ? $" status={result.StatusCode}" : "";
            var proxy = string.IsNullOrWhiteSpace(result.Proxy) ? "" : $" proxy={result.Proxy}";
            var error = string.IsNullOrWhiteSpace(result.Error) ? "" : $" error={result.Error}";
            lines.Add($"{result.Route}: {status}{code} time={result.DurationMs}ms target={result.Target}{proxy}{error}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void UpdateNetworkTestSummary(IEnumerable<NetworkTestResult> results, string? note)
    {
        var tested = results.ToList();
        var direct = tested.FirstOrDefault(x => string.Equals(x.Route, "Direct", StringComparison.OrdinalIgnoreCase));
        var proxy = tested.FirstOrDefault(x => !string.Equals(x.Route, "Direct", StringComparison.OrdinalIgnoreCase));

        if (direct?.Success == true && proxy?.Success == true)
        {
            NetworkTestSummary = "Direct + proxy ok";
            NetworkTestDetail = $"Direct {FormatResultDuration(direct)}, {proxy.Route} {FormatResultDuration(proxy)}";
            return;
        }

        if (direct?.Success == true && proxy is null)
        {
            NetworkTestSummary = "Direct ok";
            NetworkTestDetail = AppendNote($"Direct {FormatResultDuration(direct)}; proxy route skipped", note);
            return;
        }

        if (direct?.Success == true)
        {
            NetworkTestSummary = "Direct ok, proxy failed";
            NetworkTestDetail = AppendNote($"Direct {FormatResultDuration(direct)}; {proxy?.Route ?? "proxy"} failed", note);
            return;
        }

        if (proxy?.Success == true)
        {
            NetworkTestSummary = "Proxy ok, direct failed";
            NetworkTestDetail = $"{proxy.Route} {FormatResultDuration(proxy)}; direct failed";
            return;
        }

        NetworkTestSummary = "Network failed";
        NetworkTestDetail = AppendNote(FirstNonEmpty(direct?.Error, proxy?.Error, "No route succeeded"), note);
    }

    private void UpdateNetworkTestBadge()
    {
        if (NetworkTestSummary.Contains("failed", StringComparison.OrdinalIgnoreCase)
            && NetworkTestSummary.Contains("ok", StringComparison.OrdinalIgnoreCase))
        {
            NetworkTestBadgeBackground = "#92400E";
            NetworkTestBadgeForeground = "#FEF3C7";
            return;
        }

        if (NetworkTestSummary.Contains("ok", StringComparison.OrdinalIgnoreCase))
        {
            NetworkTestBadgeBackground = "#065F46";
            NetworkTestBadgeForeground = "#D1FAE5";
            return;
        }

        if (NetworkTestSummary.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || NetworkTestSummary.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        {
            NetworkTestBadgeBackground = "#7F1D1D";
            NetworkTestBadgeForeground = "#FECACA";
            return;
        }

        if (NetworkTestSummary.Contains("testing", StringComparison.OrdinalIgnoreCase)
            || NetworkTestSummary.Contains("ready", StringComparison.OrdinalIgnoreCase))
        {
            NetworkTestBadgeBackground = "#1E3A8A";
            NetworkTestBadgeForeground = "#BFDBFE";
            return;
        }

        NetworkTestBadgeBackground = "#374151";
        NetworkTestBadgeForeground = "#E5E7EB";
    }

    private static string FormatResultDuration(NetworkTestResult result)
    {
        var code = result.StatusCode.HasValue ? $" status {result.StatusCode}" : "";
        return $"{result.DurationMs}ms{code}";
    }

    private static string AppendNote(string text, string? note)
    {
        return string.IsNullOrWhiteSpace(note) ? text : $"{text}; {note}";
    }

    private bool ValidateSubscription(Subscription subscription)
    {
        subscription.DisplayName = subscription.DisplayName.Trim();
        subscription.Url = subscription.Url.Trim();
        if (string.IsNullOrWhiteSpace(subscription.DisplayName))
        {
            SubscriptionStatusText = "Subscription name is required";
            ErrorText = SubscriptionStatusText;
            return false;
        }
        if (!Uri.TryCreate(subscription.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            SubscriptionStatusText = "Subscription URL must be an absolute http or https URL";
            ErrorText = SubscriptionStatusText;
            return false;
        }
        if (subscription.UpdateIntervalMinutes <= 0)
        {
            SubscriptionStatusText = "Update interval must be greater than zero";
            ErrorText = SubscriptionStatusText;
            return false;
        }
        return true;
    }

    private static string BuildSubscriptionStatus(Subscription subscription)
    {
        var updated = subscription.LastUpdatedAt?.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) ?? "-";
        return string.Join(Environment.NewLine, new[]
        {
            $"Last result: {subscription.LastResult}",
            $"Updated: {updated}",
            $"Cache: etag={subscription.ETag ?? "-"} modified={subscription.LastModified ?? "-"}"
        });
    }

    private static string BuildSubscriptionSourceSummary(Subscription subscription)
    {
        var updated = subscription.LastUpdatedAt?.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) ?? "-";
        return string.Join(Environment.NewLine, new[]
        {
            $"Subscription: {subscription.DisplayName}",
            $"URL: {subscription.Url}",
            $"Interval: {subscription.UpdateIntervalMinutes} minute(s)",
            $"Trust: {subscription.TrustPolicy}",
            $"Last result: {subscription.LastResult}",
            $"Updated: {updated}"
        });
    }

    private void UpdateFilteredSubscriptions(Guid? preferredSubscriptionId = null)
    {
        var selectedId = preferredSubscriptionId ?? SelectedSubscription?.Id;
        var matches = ApplySubscriptionSort(Subscriptions.Where(MatchesSubscriptionSearch)).ToList();
        ReplaceCollection(FilteredSubscriptions, matches);
        UpdateSubscriptionSummary();

        if (FilteredSubscriptions.Count == 0)
        {
            SelectedSubscription = null;
            RaiseSubscriptionCommandState();
            return;
        }

        var preferred = selectedId.HasValue
            ? FilteredSubscriptions.FirstOrDefault(x => x.Id == selectedId.Value)
            : null;
        SelectedSubscription = preferred ?? FilteredSubscriptions.First();
    }

    private bool MatchesSubscriptionSearch(Subscription subscription)
    {
        var filter = SubscriptionSearchText.Trim();
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return SubscriptionFieldContains(subscription.DisplayName, filter)
            || SubscriptionFieldContains(subscription.Url, filter)
            || SubscriptionFieldContains(subscription.LastResult, filter)
            || SubscriptionFieldContains(subscription.UpdateState, filter)
            || SubscriptionFieldContains(subscription.UpdateDetail, filter)
            || SubscriptionFieldContains(subscription.TrustPolicy, filter);
    }

    private static bool SubscriptionFieldContains(string? value, string filter)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<Subscription> ApplySubscriptionSort(IEnumerable<Subscription> subscriptions)
    {
        return SelectedSubscriptionSort switch
        {
            "Name" => subscriptions.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase),
            "Updated" => subscriptions
                .OrderByDescending(x => x.LastUpdatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase),
            "Status" => subscriptions
                .OrderBy(SubscriptionStatusSortGroup)
                .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => subscriptions
        };
    }

    private static int SubscriptionStatusSortGroup(Subscription subscription)
    {
        return subscription.UpdateState switch
        {
            "Failed" => 0,
            "New" => 1,
            _ => 2
        };
    }

    private void UpdateSubscriptionSummary()
    {
        if (Subscriptions.Count == 0)
        {
            SubscriptionSummaryText = "Subscriptions 0/0 visible, 0 updated, 0 failed";
            return;
        }

        var updated = Subscriptions.Count(x => x.LastUpdatedAt.HasValue);
        var failed = Subscriptions.Count(x => x.LastResult.StartsWith("failed:", StringComparison.OrdinalIgnoreCase));
        SubscriptionSummaryText = $"Subscriptions {FilteredSubscriptions.Count}/{Subscriptions.Count} visible, {updated} updated, {failed} failed";
    }

    private static bool TryGetNetworkTestTargetUrl(string target, out string url)
    {
        url = target switch
        {
            "Google 204" => "https://www.gstatic.com/generate_204",
            "Microsoft NCSI" => "http://www.msftconnecttest.com/connecttest.txt",
            "Cloudflare Trace" => "https://www.cloudflare.com/cdn-cgi/trace",
            "Firefox Success" => "http://detectportal.firefox.com/success.txt",
            _ => ""
        };
        return !string.IsNullOrWhiteSpace(url);
    }

    private static string FindNetworkTestTargetForUrl(string url)
    {
        foreach (var target in new[] { "Google 204", "Microsoft NCSI", "Cloudflare Trace", "Firefox Success" })
        {
            if (TryGetNetworkTestTargetUrl(target, out var targetUrl)
                && string.Equals(url.Trim(), targetUrl, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }
        }
        return "Custom";
    }

    private void UpdateDiagnosticsPortStatus(DiagnosticReport report)
    {
        if (report.ActiveProfile is null)
        {
            SetDiagnosticsPortStatus("Ports: no profile", "Select a profile before running diagnostics.", "#92400E", "#FEF3C7");
            return;
        }

        if (report.PortChecks.Count == 0)
        {
            var warning = report.Warnings.FirstOrDefault(x => x.Contains("port check failed", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(warning))
            {
                SetDiagnosticsPortStatus("Ports: check failed", warning, "#7F1D1D", "#FECACA");
                return;
            }

            SetDiagnosticsPortStatus("Ports: none", "Selected profile has no local listen endpoint.", "#374151", "#E5E7EB");
            return;
        }

        var detail = string.Join("; ", report.PortChecks.Select(FormatPortCheck));
        if (report.PortChecks.Any(x => !x.Available))
        {
            SetDiagnosticsPortStatus("Ports: blocked", detail, "#7F1D1D", "#FECACA");
            return;
        }

        SetDiagnosticsPortStatus("Ports: available", detail, "#065F46", "#D1FAE5");
    }

    private void SetDiagnosticsPortStatus(string status, string detail, string background, string foreground)
    {
        DiagnosticsPortStatus = status;
        DiagnosticsPortDetail = detail;
        DiagnosticsPortBackground = background;
        DiagnosticsPortForeground = foreground;
    }

    private static string FormatPortCheck(PortCheckResult result)
    {
        return result.Available
            ? $"{result.Address} ok"
            : $"{result.Address} blocked ({result.Error})";
    }

    private static string BuildDiagnosticsSummary(DiagnosticReport report)
    {
        var lines = new List<string>
        {
            $"Created: {report.CreatedAt.LocalDateTime:g}",
            $"OS: {report.OsVersion} / {report.Architecture}",
            $"GUI: {report.GuiVersion}",
            $"Profile: {report.ActiveProfile?.Name ?? "-"}",
            $"Core: {report.Status?.Version ?? "-"} {report.Status?.Mode ?? ""}".Trim(),
            $"Proxy: enable={report.CurrentProxy?.ProxyEnable ?? 0} server={report.CurrentProxy?.ProxyServer ?? "-"} pac={report.CurrentProxy?.AutoConfigUrl ?? "-"}"
        };
        if (report.PortChecks.Count > 0)
        {
            lines.Add("Ports: " + string.Join("; ", report.PortChecks.Select(FormatPortCheck)));
        }
        if (report.Warnings.Count > 0)
        {
            lines.Add("Warnings: " + string.Join("; ", report.Warnings));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static void ApplyTheme(string? theme)
    {
        if (Application.Current is null)
        {
            return;
        }
        Application.Current.RequestedThemeVariant = theme?.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
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

    private void ReportProfileError(Exception ex)
    {
        if (SelectedProfile is null)
        {
            ErrorText = ex.Message;
            return;
        }
        var issue = BuildProfileIssue(ex);
        SelectedProfile.LastValidationError = issue.Message;
        RefreshProfileListItem(SelectedProfile);
        ReplaceCollection(ProfileIssues, [issue]);
        ErrorText = issue.Message;
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

    private bool HasNetworkTestResult()
    {
        return !string.IsNullOrWhiteSpace(NetworkTestText)
            && !string.Equals(NetworkTestText, "Not tested", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(NetworkTestText, "Testing network...", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasProfileEndpointTestResult()
    {
        return !string.IsNullOrWhiteSpace(ProfileEndpointTestText)
            && !string.Equals(ProfileEndpointTestText, "Not tested", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ProfileEndpointTestText, "Testing profile forward endpoint...", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ProfileEndpointTestText, "No selected profile", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasFilteredLogs()
    {
        return !string.IsNullOrWhiteSpace(FilteredLogText);
    }

    private bool HasDiagnosticsSummary()
    {
        return !string.IsNullOrWhiteSpace(DiagnosticsSummaryText);
    }

    private bool HasDiagnosticsReport()
    {
        return !string.IsNullOrWhiteSpace(DiagnosticsText);
    }

    private bool HasDiagnosticsPortDetail()
    {
        return !string.IsNullOrWhiteSpace(DiagnosticsPortDetail);
    }

    private bool HasDiagnosticsTestResults()
    {
        return HasNetworkTestResult() || HasProfileEndpointTestResult();
    }

    private bool HasOverviewRecentLogs()
    {
        return !string.IsNullOrWhiteSpace(LogText);
    }

    private bool HasOverviewRuntimeDetails()
    {
        return !string.IsNullOrWhiteSpace(StatusText)
            && !string.Equals(StatusText, "Stopped", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasRuntimeControl()
    {
        return _supervisor.Control is not null && RuntimeState is RuntimeState.Running or RuntimeState.Degraded;
    }

    private bool HasSelectedProfileConfig()
    {
        return !string.IsNullOrWhiteSpace(SelectedProfile?.CoreConfigJson);
    }

    private bool HasCorePathToCopy()
    {
        return !string.IsNullOrWhiteSpace(FirstNonEmpty(CorePath, DetectedCorePath));
    }

    private bool HasSubscriptionStatusResult()
    {
        return !string.IsNullOrWhiteSpace(SubscriptionStatusText)
            && !string.Equals(SubscriptionStatusText, "No subscriptions configured", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(SubscriptionStatusText, "No subscription selected", StringComparison.OrdinalIgnoreCase)
            && !SubscriptionStatusText.StartsWith("Fetching subscription", StringComparison.OrdinalIgnoreCase)
            && !SubscriptionStatusText.StartsWith("Updating ", StringComparison.OrdinalIgnoreCase);
    }

    private void RaiseCommandState()
    {
        DuplicateProfileCommand.RaiseCanExecuteChanged();
        DeleteProfileCommand.RaiseCanExecuteChanged();
        SaveProfileCommand.RaiseCanExecuteChanged();
        ValidateProfileCommand.RaiseCanExecuteChanged();
        FormatProfileCommand.RaiseCanExecuteChanged();
        CopyProfileSummaryCommand.RaiseCanExecuteChanged();
        CopyProfileConfigCommand.RaiseCanExecuteChanged();
        CopyProfileIssuesCommand.RaiseCanExecuteChanged();
        TestSelectedProfileEndpointCommand.RaiseCanExecuteChanged();
        UseSelectedProfileAtStartupCommand.RaiseCanExecuteChanged();
        ClearStartupProfileCommand.RaiseCanExecuteChanged();
        ApplyFormCommand.RaiseCanExecuteChanged();
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
        TestProfileEndpointCommand.RaiseCanExecuteChanged();
    }

    private void RaiseSubscriptionCommandState()
    {
        SaveSubscriptionCommand.RaiseCanExecuteChanged();
        DeleteSubscriptionCommand.RaiseCanExecuteChanged();
        RefreshSubscriptionCommand.RaiseCanExecuteChanged();
        RefreshAllSubscriptionsCommand.RaiseCanExecuteChanged();
        CopySubscriptionStatusCommand.RaiseCanExecuteChanged();
        CopySubscriptionSourceCommand.RaiseCanExecuteChanged();
    }
}
