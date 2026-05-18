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

public sealed class ProfileListRow
{
    public Profile Profile { get; init; } = new();
    public string Name { get; init; } = "";
    public string ListSubtitle { get; init; } = "";
    public string ValidationState { get; init; } = "";
    public string ValidationDetail { get; init; } = "";
    public string ValidationBadgeBackground { get; init; } = "";
    public string ValidationBadgeForeground { get; init; } = "";
    public string EndpointTestState { get; init; } = "";
    public string EndpointTestDetail { get; init; } = "";
    public string EndpointBadgeBackground { get; init; } = "";
    public string EndpointBadgeForeground { get; init; } = "";
}

public sealed class SubscriptionListRow
{
    public Subscription Subscription { get; init; } = new();
    public string DisplayName { get; init; } = "";
    public string ListSubtitle { get; init; } = "";
    public string UpdateState { get; init; } = "";
    public string UpdateDetail { get; init; } = "";
    public string UpdateBadgeBackground { get; init; } = "";
    public string UpdateBadgeForeground { get; init; } = "";
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

    private AppText _text = AppText.For("en-US");
    private Profile? _selectedProfile;
    private ProfileListRow? _selectedProfileRow;
    private Subscription? _selectedSubscription;
    private SubscriptionListRow? _selectedSubscriptionRow;
    private AppSettings _settings;
    private RuntimeState _runtimeState = RuntimeState.Stopped;
    private string _statusText = "Stopped";
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
    private string _appMessageForeground = "#E5E7EB";
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
    private bool _syncingProfileRowSelection;
    private bool _syncingSubscriptionRowSelection;

    public MainViewModel()
    {
        _repository = new ProfileRepository(_paths);
        _settings = _repository.GetSettings();
        ApplyLanguage(_settings.Language, refresh: false);
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
        CheckCoreVersionCommand = new AsyncRelayCommand(CheckCoreVersionAsync);
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

    public AppText T
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public ObservableCollection<Profile> Profiles { get; } = [];
    public ObservableCollection<Profile> FilteredProfiles { get; } = [];
    public ObservableCollection<ProfileListRow> FilteredProfileRows { get; } = [];
    public ObservableCollection<DashboardMetric> OverviewMetrics { get; } = [];
    public ObservableCollection<SummaryRow> ListenerRows { get; } = [];
    public ObservableCollection<SummaryRow> ChannelRows { get; } = [];
    public ObservableCollection<ProfileIssue> ProfileIssues { get; } = [];
    public ObservableCollection<Subscription> Subscriptions { get; } = [];
    public ObservableCollection<Subscription> FilteredSubscriptions { get; } = [];
    public ObservableCollection<SubscriptionListRow> FilteredSubscriptionRows { get; } = [];
    public ObservableCollection<SelectOption<ProxyMode>> ProxyModeOptions { get; } = [];
    public IReadOnlyList<string> ProfileKinds { get; } = ["client", "server"];
    public ObservableCollection<SelectOption<string>> ThemeOptions { get; } = [];
    public ObservableCollection<SelectOption<string>> UpdateChannels { get; } = [];
    public IReadOnlyList<string> SubscriptionTrustPolicies { get; } = ["confirm", "auto"];
    public ObservableCollection<SelectOption<string>> LogLevelFilters { get; } = [];
    public ObservableCollection<SelectOption<string>> ProfileSortOptions { get; } = [];
    public ObservableCollection<SelectOption<string>> SubscriptionSortOptions { get; } = [];
    public IReadOnlyList<string> NetworkTestTargets { get; } = ["Google 204", "Microsoft NCSI", "Cloudflare Trace", "Firefox Success", "Custom"];
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } = AppText.LanguageOptions;

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
                SyncSelectedProfileRow();
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
                SubscriptionStatusText = value is null ? NoSubscriptionSelectedText : BuildSubscriptionStatus(value);
                RaiseSubscriptionCommandState();
                SyncSelectedSubscriptionRow();
            }
        }
    }

    public ProfileListRow? SelectedProfileRow
    {
        get => _selectedProfileRow;
        set
        {
            if (SetProperty(ref _selectedProfileRow, value) && !_syncingProfileRowSelection)
            {
                SelectedProfile = value?.Profile;
            }
        }
    }

    public SubscriptionListRow? SelectedSubscriptionRow
    {
        get => _selectedSubscriptionRow;
        set
        {
            if (SetProperty(ref _selectedSubscriptionRow, value) && !_syncingSubscriptionRowSelection)
            {
                SelectedSubscription = value?.Subscription;
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
                OnPropertyChanged(nameof(SelectedProxyModeOption));
                RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
            }
        }
    }

    public SelectOption<ProxyMode>? SelectedProxyModeOption
    {
        get => FindOption(ProxyModeOptions, SelectedProxyMode);
        set
        {
            if (value is not null)
            {
                SelectedProxyMode = value.Value;
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
                OnPropertyChanged(nameof(RuntimeStateText));
                RaiseCommandState();
            }
        }
    }

    public string RuntimeStateText => RuntimeState switch
    {
        RuntimeState.Running => T.Connected,
        RuntimeState.Degraded => T.Degraded,
        RuntimeState.Starting => T.Starting,
        RuntimeState.Stopping => T.Stopping,
        RuntimeState.Faulted => T.Faulted,
        RuntimeState.Recovering => T.Recovering,
        _ => T.Disconnected
    };

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
                OnPropertyChanged(nameof(SelectedLogLevelFilterOption));
                UpdateFilteredLogText();
            }
        }
    }

    public SelectOption<string>? SelectedLogLevelFilterOption
    {
        get => FindOption(LogLevelFilters, SelectedLogLevelFilter);
        set
        {
            if (value is not null)
            {
                SelectedLogLevelFilter = value.Value;
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
                OnPropertyChanged(nameof(SelectedProfileSortOption));
                UpdateFilteredProfiles();
            }
        }
    }

    public SelectOption<string>? SelectedProfileSortOption
    {
        get => FindOption(ProfileSortOptions, SelectedProfileSort);
        set
        {
            if (value is not null)
            {
                SelectedProfileSort = value.Value;
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
                OnPropertyChanged(nameof(SelectedSubscriptionSortOption));
                UpdateFilteredSubscriptions();
            }
        }
    }

    public SelectOption<string>? SelectedSubscriptionSortOption
    {
        get => FindOption(SubscriptionSortOptions, SelectedSubscriptionSort);
        set
        {
            if (value is not null)
            {
                SelectedSubscriptionSort = value.Value;
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
            NetworkTestSummary = L("Ready to test", "准备测试");
            NetworkTestDetail = trimmed;
            NetworkTestLastRunText = L("Target changed; run test", "目标已更改，请运行测试");
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
        set
        {
            if (SetProperty(ref _errorText, value))
            {
                AppMessageForeground = ClassifyAppMessageForeground(value);
            }
        }
    }

    public string AppMessageForeground
    {
        get => _appMessageForeground;
        private set => SetProperty(ref _appMessageForeground, value);
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
                OnPropertyChanged(nameof(SelectedThemeOption));
                OnPropertyChanged();
            }
        }
    }

    public SelectOption<string>? SelectedThemeOption
    {
        get => FindOption(ThemeOptions, SelectedTheme);
        set
        {
            if (value is not null)
            {
                SelectedTheme = value.Value;
            }
        }
    }

    public LanguageOption SelectedLanguage
    {
        get
        {
            var language = AppText.NormalizeLanguage(Settings.Language);
            return LanguageOptions.FirstOrDefault(x => string.Equals(x.Code, language, StringComparison.OrdinalIgnoreCase))
                ?? LanguageOptions[0];
        }
        set
        {
            if (value is null)
            {
                return;
            }
            if (!string.Equals(AppText.NormalizeLanguage(Settings.Language), value.Code, StringComparison.OrdinalIgnoreCase))
            {
                ApplyLanguage(value.Code);
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
                OnPropertyChanged(nameof(SelectedUpdateChannelOption));
                OnPropertyChanged();
            }
        }
    }

    public SelectOption<string>? SelectedUpdateChannelOption
    {
        get => FindOption(UpdateChannels, SelectedUpdateChannel);
        set
        {
            if (value is not null)
            {
                SelectedUpdateChannel = value.Value;
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
                return T.LanguageCode == "zh-CN" ? "启动：未设置" : "Startup: not set";
            }

            var profile = Profiles.FirstOrDefault(x => x.Id == Settings.AutoConnectProfileId.Value);
            if (profile is null)
            {
                return T.LanguageCode == "zh-CN" ? "启动：已保存配置缺失" : "Startup: saved profile missing";
            }
            return T.LanguageCode == "zh-CN" ? $"启动：{profile.Name}" : $"Startup: {profile.Name}";
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
    public AsyncRelayCommand CheckCoreVersionCommand { get; }
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
        StatusText = StoppedText;
        RefreshTransientLocalizedUi();
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
            SubscriptionStatusText = NoSubscriptionsConfiguredText;
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
        summary.AppendLine($"{T.Profile}: {profile.Name}");
        summary.AppendLine($"{L("Kind", "类型")}: {profile.Kind}");
        summary.AppendLine($"{L("Source", "来源")}: {profile.Source}");
        summary.AppendLine($"{T.Validation}: {LocalizeValidationState(profile)} - {LocalizeValidationDetail(profile)}");
        summary.AppendLine($"{L("Endpoint test", "端点测试")}: {LocalizeEndpointTestState(profile)} - {LocalizeEndpointTestDetail(profile)}");

        try
        {
            var endpoints = _configService.GetLocalProxyEndpoints(profile.CoreConfigJson);
            summary.AppendLine($"{L("Local proxy", "本地代理")}: {BuildProxyEndpointSummary(endpoints)}");
        }
        catch (Exception ex)
        {
            summary.AppendLine($"{L("Local proxy", "本地代理")}: {ex.Message}");
        }

        try
        {
            var endpoint = _configService.GetForwardEndpoint(profile.CoreConfigJson);
            summary.AppendLine($"{L("Forward", "转发")}: {endpoint.Display}");
        }
        catch (Exception ex)
        {
            summary.AppendLine($"{L("Forward", "转发")}: {ex.Message}");
        }

        summary.AppendLine($"{L("Config", "配置")}:");
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
        summary.AppendLine($"{T.Profile}: {profile.Name}");
        summary.AppendLine($"{L("Kind", "类型")}: {profile.Kind}");
        summary.AppendLine($"{L("Source", "来源")}: {profile.Source}");
        summary.AppendLine($"{T.Validation}: {LocalizeValidationState(profile)} - {LocalizeValidationDetail(profile)}");
        if (!string.IsNullOrWhiteSpace(profile.LastValidationError))
        {
            summary.AppendLine($"{L("Last error", "上次错误")}: {profile.LastValidationError}");
        }

        if (ProfileIssues.Count == 0)
        {
            summary.AppendLine(IsChinese ? "问题: 无" : "Issues: none");
        }
        else
        {
            summary.AppendLine($"{T.Issue}:");
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
        summary.AppendLine($"{L("Status", "状态")}: {ConnectionHeading}");
        summary.AppendLine($"{L("Detail", "详情")}: {ConnectionDetail}");
        summary.AppendLine($"{T.Profile}: {ActiveProfileSummary}");
        summary.AppendLine($"{T.ProxyMode}: {ProxySummary}");
        summary.AppendLine($"{L("Local proxy", "本地代理")}: {LocalProxySummary}");
        summary.AppendLine($"{T.Traffic}: {StatusBarTrafficSummary}");
        summary.AppendLine($"{T.Channels}: {StatusBarChannelSummary}");
        summary.AppendLine($"{T.Network}: {NetworkTestSummary}");
        summary.AppendLine($"{L("Network detail", "网络详情")}: {NetworkTestDetail}");
        summary.AppendLine($"{L("Network updated", "网络更新时间")}: {NetworkTestLastRunText}");
        summary.AppendLine($"{L("Network target", "网络目标")}: {NetworkTestUrl}");
        summary.AppendLine($"{T.Core}: {CoreSummary}");
        summary.AppendLine($"{T.Validation}: {ValidationSummary}");
        summary.AppendLine($"{T.Issue}: {RecentIssueSummary}");
        return summary.ToString().TrimEnd();
    }

    public string BuildSettingsFoldersSummary()
    {
        var summary = new StringBuilder();
        summary.AppendLine($"{L("App data", "应用数据")}: {_paths.Root}");
        summary.AppendLine($"{T.Profiles}: {_paths.Profiles}");
        summary.AppendLine($"{T.Logs}: {_paths.Logs}");
        summary.AppendLine($"{T.Runtime}: {_paths.Runtime}");
        summary.AppendLine($"{T.Core}: {FirstNonEmpty(CorePath, DetectedCorePath, "-")}");
        summary.AppendLine($"{L("Core status", "内核状态")}: {CorePathStatus}");
        summary.AppendLine($"{L("Core detail", "内核详情")}: {CorePathStatusDetail}");
        return summary.ToString().TrimEnd();
    }

    public async Task<string> ExportDiagnosticsAsync()
    {
        var report = await _diagnostics.CreateReportAsync(SelectedProfile, _supervisor.Control);
        DiagnosticsSummaryText = BuildDiagnosticsSummary(report);
        DiagnosticsText = JsonSerializer.Serialize(report, JsonDefaults.Pretty);
        UpdateDiagnosticsPortStatus(report);
        DiagnosticsLastRunText = TimeStatus("Checks refreshed", "检查已刷新");
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
        SubscriptionStatusText = L("Subscription saved", "订阅已保存");
        ErrorText = SubscriptionStatusText;
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
        SubscriptionStatusText = L("Subscription deleted", "订阅已删除");
        RaiseSubscriptionCommandState();
    }

    private async Task RefreshSelectedSubscriptionAsync()
    {
        if (SelectedSubscription is null || !ValidateSubscription(SelectedSubscription))
        {
            return;
        }

        var subscription = SelectedSubscription;
        SubscriptionStatusText = L("Fetching subscription...", "正在获取订阅...");
        var result = await UpdateSubscriptionAsync(subscription);
        LoadProfiles(SelectedProfile?.Id);
        LoadSubscriptions(subscription.Id);
        SubscriptionStatusText = BuildSubscriptionStatus(SelectedSubscription ?? subscription);
        ErrorText = result.Success
            ? result.NotModified ? "" : L("Subscription updated", "订阅已更新")
            : result.Message;
    }

    private async Task RefreshAllSubscriptionsAsync()
    {
        if (Subscriptions.Count == 0)
        {
            SubscriptionStatusText = NoSubscriptionsConfiguredText;
            ErrorText = SubscriptionStatusText;
            return;
        }

        var selectedId = SelectedSubscription?.Id;
        SubscriptionStatusText = IsChinese
            ? $"正在更新 {Subscriptions.Count} 个订阅..."
            : $"Updating {Subscriptions.Count} subscription(s)...";
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
        SubscriptionStatusText = IsChinese
            ? string.Join(Environment.NewLine,
            [
                $"已更新全部 {total} 个订阅",
                $"成功: {succeeded}，失败: {failed}",
                $"新增: {added}，更新: {updated}，未变更: {unchanged}，未修改: {notModified}"
            ])
            : string.Join(Environment.NewLine,
            [
                $"Updated all {total} subscription(s)",
                $"Succeeded: {succeeded}, failed: {failed}",
                $"Added: {added}, updated: {updated}, unchanged: {unchanged}, not modified: {notModified}"
            ]);
        ErrorText = failed == 0 ? L("All subscriptions updated", "全部订阅已更新") : L("Some subscriptions failed", "部分订阅更新失败");
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
            ErrorText = L("Profile saved", "配置已保存");
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
            ErrorText = L("Structured fields applied to JSON", "结构化字段已应用到 JSON");
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
                    Message = L("Core path not found; only local JSON validation was run.", "未找到内核路径；仅运行本地 JSON 校验。")
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
                ? [new ProfileIssue { Field = "profile", Severity = "ok", Message = L("Core config check and local port checks passed.", "内核配置检查和本地端口检查已通过。") }]
                : issues);
            ErrorText = hasErrors
                ? L("Profile has blocking validation issues", "配置存在阻塞校验问题")
                : hasWarnings ? L("Profile validated with warnings", "配置校验通过但有警告") : L("Profile ready", "配置就绪");
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
                ErrorText = L("Formatted with core", "已使用内核格式化");
            }
            else
            {
                SelectedProfile.CoreConfigJson = obj.ToJsonString(JsonDefaults.Pretty);
                ErrorText = L("Core path not found; formatted locally", "未找到内核路径；已在本地格式化");
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
            StatusText = T.Faulted;
            RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
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
        DiagnosticsLastRunText = TimeStatus("Checks refreshed", "检查已刷新");
        ErrorText = L("Diagnostics refreshed", "诊断已刷新");
    }

    private async Task OpenDiagnosticsAsync()
    {
        SelectedMainTabIndex = 4;
        await RefreshDiagnosticsAsync();
    }

    private async Task RunAllDiagnosticsAsync()
    {
        SelectedMainTabIndex = 4;
        ErrorText = L("Running diagnostics...", "正在运行诊断...");
        await RefreshDiagnosticsAsync();
        await TestNetworkAsync();
        await TestProfileEndpointAsync();
        ErrorText = L("Diagnostics run complete", "诊断运行完成");
    }

    private void CopyDiagnosticsSummary()
    {
        if (!HasDiagnosticsSummary())
        {
            return;
        }

        CopyTextRequested?.Invoke(this, BuildDiagnosticsShareSummary());
        ErrorText = L("Diagnostics summary copied", "诊断摘要已复制");
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
            lines.Add($"{T.Network}: {NetworkTestSummary} ({NetworkTestLastRunText})");
            lines.Add($"{L("Network detail", "网络详情")}: {NetworkTestDetail}");
        }

        if (HasProfileEndpointTestResult())
        {
            lines.Add($"{L("Forward", "转发")}: {ProfileEndpointRouteStatus} ({ProfileEndpointTestLastRunText})");
            lines.Add($"{L("Forward detail", "转发详情")}: {ProfileEndpointRouteDetail}");
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
        ErrorText = L("Diagnostics report copied", "诊断报告已复制");
    }

    private void CopyDiagnosticsPorts()
    {
        if (!HasDiagnosticsPortDetail())
        {
            return;
        }

        CopyTextRequested?.Invoke(this, $"{DiagnosticsPortStatus}{Environment.NewLine}{DiagnosticsPortDetail}");
        ErrorText = L("Diagnostics ports copied", "诊断端口结果已复制");
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
            NetworkTestText = L("Target URL must be an absolute http or https URL", "目标 URL 必须是绝对 http 或 https URL");
            NetworkTestSummary = L("Invalid target", "无效目标");
            NetworkTestDetail = NetworkTestText;
            NetworkTestLastRunText = TimeStatus("Last attempted", "上次尝试");
            SetNetworkRouteInvalid(NetworkTestText);
            ErrorText = NetworkTestText;
            return;
        }

        try
        {
            var endpoints = TryGetSelectedLocalProxyEndpoints(out var endpointError);
            NetworkTestText = TestingNetworkText;
            NetworkTestSummary = TestingNetworkText;
            NetworkTestDetail = target.ToString();
            NetworkTestLastRunText = TimeStatus("Testing started", "测试开始");
            SetNetworkRouteTesting(target, endpoints, endpointError);
            var results = await _networkTester.TestAsync(target, endpoints);
            NetworkTestText = FormatNetworkTestResults(results, endpointError);
            UpdateNetworkRouteStatus(results, endpointError);
            UpdateNetworkTestSummary(results, endpointError);
            NetworkTestLastRunText = TimeStatus("Last tested", "上次测试");
            ErrorText = results.Any(x => x.Success) ? "" : L("Network test failed", "网络测试失败");
        }
        catch (Exception ex)
        {
            NetworkTestText = IsChinese ? $"网络测试失败: {ex.Message}" : $"Network test failed: {ex.Message}";
            NetworkTestSummary = L("Network failed", "网络失败");
            NetworkTestDetail = ex.Message;
            NetworkTestLastRunText = TimeStatus("Last failed", "上次失败");
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
        ErrorText = L("Network test result copied", "网络测试结果已复制");
    }

    private void ClearNetworkTest()
    {
        NetworkTestText = NetworkNotTestedText;
        NetworkTestSummary = NetworkNotTestedText;
        NetworkTestDetail = DefaultNetworkTestDetailText;
        NetworkTestLastRunText = L("Network test not run", "网络测试未运行");
        SetNetworkRouteNotTested();
        ErrorText = L("Network test result cleared", "网络测试结果已清除");
    }

    private void ClearDiagnosticsTests()
    {
        ClearNetworkTest();
        ClearProfileEndpointTest();
        ErrorText = L("Diagnostics test results cleared", "诊断测试结果已清除");
    }

    private void CopyProfileEndpointTestResult()
    {
        if (!HasProfileEndpointTestResult())
        {
            return;
        }
        CopyTextRequested?.Invoke(this, ProfileEndpointTestText);
        ErrorText = L("Profile endpoint test result copied", "配置端点测试结果已复制");
    }

    private void ClearProfileEndpointTest()
    {
        ProfileEndpointTestText = NetworkNotTestedText;
        ProfileEndpointTestLastRunText = L("Forward test not run", "转发测试未运行");
        SetProfileEndpointRouteNotTested();
        ErrorText = L("Profile endpoint test result cleared", "配置端点测试结果已清除");
    }

    private async Task TestProfileEndpointAsync()
    {
        if (SelectedProfile is null)
        {
            ProfileEndpointTestText = L("No selected profile", "未选择配置");
            ProfileEndpointTestLastRunText = TimeStatus("Last attempted", "上次尝试");
            SetProfileEndpointRoute(L("Forward TCP: unavailable", "转发 TCP：不可用"), ProfileEndpointTestText, RouteVisual.Warning);
            return;
        }

        try
        {
            ProfileEndpointTestText = TestingProfileEndpointText;
            ProfileEndpointTestLastRunText = TimeStatus("Testing started", "测试开始");
            var endpoint = _configService.GetForwardEndpoint(SelectedProfile.CoreConfigJson);
            SetProfileEndpointRoute(L("Forward TCP: testing", "转发 TCP：测试中"), endpoint.Display, RouteVisual.Busy);
            var result = await _networkTester.TestEndpointAsync(endpoint);
            ProfileEndpointTestText = FormatNetworkTestResults([result], note: null);
            UpdateProfileEndpointRoute(result);
            ProfileEndpointTestLastRunText = TimeStatus("Last tested", "上次测试");
            ErrorText = result.Success ? "" : L("Profile endpoint test failed", "配置端点测试失败");
        }
        catch (Exception ex)
        {
            ProfileEndpointTestText = IsChinese ? $"配置端点测试失败: {ex.Message}" : $"Profile endpoint test failed: {ex.Message}";
            ProfileEndpointTestLastRunText = TimeStatus("Last failed", "上次失败");
            SetProfileEndpointRoute(L("Forward TCP: failed", "转发 TCP：失败"), ex.Message, RouteVisual.Failed);
            ErrorText = ex.Message;
        }
    }

    private async Task TestVisibleProfilesAsync()
    {
        var profiles = FilteredProfiles.ToList();
        if (profiles.Count == 0)
        {
            ProfileBatchTestText = L("No visible profiles to test", "没有可见配置可测试");
            return;
        }

        ProfileBatchTestText = IsChinese
            ? $"正在测试 {profiles.Count} 个可见配置..."
            : $"Testing {profiles.Count} visible profile(s)...";
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

        ProfileBatchTestText = IsChinese
            ? $"端点测试: {ok} 正常，{failed} 失败"
            : $"Endpoint tests: {ok} ok, {failed} failed";
        ErrorText = failed == 0 ? "" : ProfileBatchTestText;
        SelectFastestProfileCommand.RaiseCanExecuteChanged();
        ClearVisibleProfileEndpointTestsCommand.RaiseCanExecuteChanged();
        RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
    }

    private async Task TestSelectedProfileEndpointAsync()
    {
        if (SelectedProfile is null)
        {
            ProfileBatchTestText = L("No selected profile to test", "没有选中的配置可测试");
            ErrorText = ProfileBatchTestText;
            return;
        }

        var profile = SelectedProfile;
        ProfileBatchTestText = IsChinese
            ? $"正在测试选中配置: {profile.Name}..."
            : $"Testing selected profile: {profile.Name}...";
        var result = await TestAndStoreProfileEndpointAsync(profile);
        if (result.Success)
        {
            ProfileBatchTestText = IsChinese
                ? $"选中端点正常: {profile.Name} ({result.DurationMs}ms)"
                : $"Selected endpoint ok: {profile.Name} ({result.DurationMs}ms)";
            ErrorText = "";
        }
        else
        {
            var error = FirstNonEmpty(result.Error, L("Endpoint test failed", "端点测试失败"));
            ProfileBatchTestText = IsChinese
                ? $"选中端点失败: {profile.Name} - {error}"
                : $"Selected endpoint failed: {profile.Name} - {error}";
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
            ProfileBatchTestText = L("No successful endpoint test result; run Test Visible first.", "没有成功的端点测试结果；请先运行测试可见配置。");
            ErrorText = ProfileBatchTestText;
            return;
        }

        SelectedProfile = fastest;
        ProfileBatchTestText = IsChinese
            ? $"已选择最快: {fastest.Name} ({fastest.LastEndpointTestDurationMs ?? 0}ms)"
            : $"Selected fastest: {fastest.Name} ({fastest.LastEndpointTestDurationMs ?? 0}ms)";
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

        ProfileBatchTestText = IsChinese
            ? $"已清除 {profiles.Count} 个可见配置的端点结果"
            : $"Cleared endpoint results for {profiles.Count} visible profile(s)";
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
        Settings.Language = AppText.NormalizeLanguage(Settings.Language);
        _repository.SaveSettings(Settings);
        ApplyLanguage(Settings.Language);
        ApplyTheme(Settings.Theme);
        var exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
        _startupService.SetEnabled(Settings.LaunchAtLogin, exe, Settings.StartMinimized);
        ErrorText = T.SettingsSaved;
    }

    private void SetProxyMode(ProxyMode mode)
    {
        SelectedProxyMode = mode;
        SaveSettings();
        ErrorText = IsChinese ? $"代理模式：{FormatProxyMode(mode)}" : $"Proxy mode: {FormatProxyMode(mode)}";
        RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
    }

    private void UseDetectedCorePath()
    {
        DetectedCorePath = _coreLocator.Resolve(new AppSettings()) ?? "";
        if (string.IsNullOrWhiteSpace(DetectedCorePath))
        {
            UpdateCorePathStatus();
            ErrorText = L("No x-tunnel.exe was auto-detected", "未自动检测到 x-tunnel.exe");
            return;
        }
        Settings.CorePath = DetectedCorePath;
        OnPropertyChanged(nameof(CorePath));
        UpdateCorePathStatus();
        ErrorText = L("Core path set from auto-detect", "已使用自动检测的内核路径");
    }

    private async Task CheckCoreVersionAsync()
    {
        var path = FirstNonEmpty(CorePath, DetectedCorePath, _coreLocator.Resolve(new AppSettings())).Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            SetCorePathStatus(L("Core missing", "内核缺失"), L("Set or auto-detect x-tunnel.exe before checking the version.", "检查版本前请设置或自动检测 x-tunnel.exe。"), "#7F1D1D", "#FECACA");
            ErrorText = L("No x-tunnel.exe was auto-detected", "未自动检测到 x-tunnel.exe");
            return;
        }

        try
        {
            var version = await _coreConfigTool.GetVersionAsync(path);
            SetCorePathStatus(L("Version OK", "版本正常"), $"{version} | {path}", "#065F46", "#D1FAE5");
            ErrorText = L("Core version checked", "内核版本已检查");
        }
        catch (Exception ex)
        {
            SetCorePathStatus(L("Version failed", "版本检查失败"), RuntimeConfigService.Redact(ex.Message), "#7F1D1D", "#FECACA");
            ErrorText = IsChinese ? $"内核版本检查失败: {ex.Message}" : $"Core version check failed: {ex.Message}";
        }
    }

    private void CopyCorePath()
    {
        var path = FirstNonEmpty(CorePath, DetectedCorePath).Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        CopyTextRequested?.Invoke(this, path);
        ErrorText = L("Core path copied", "内核路径已复制");
    }

    private void CopySettingsFolders()
    {
        CopyTextRequested?.Invoke(this, BuildSettingsFoldersSummary());
        ErrorText = L("Settings folders copied", "设置目录已复制");
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
            ErrorText = L("Proxy address copied", "代理地址已复制");
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
        ErrorText = L("Overview status copied", "概览状态已复制");
    }

    private void CopyOverviewRecentLogs()
    {
        if (!HasOverviewRecentLogs())
        {
            return;
        }

        CopyTextRequested?.Invoke(this, LogText);
        ErrorText = L("Overview recent logs copied", "概览最近日志已复制");
    }

    private void CopyOverviewRuntimeDetails()
    {
        if (!HasOverviewRuntimeDetails())
        {
            return;
        }

        CopyTextRequested?.Invoke(this, BuildOverviewRuntimeDetailsShare());
        ErrorText = L("Overview runtime details copied", "概览运行详情已复制");
    }

    private string BuildOverviewRuntimeDetailsShare()
    {
        var summary = new StringBuilder();
        summary.AppendLine($"{L("Status", "状态")}:");
        summary.AppendLine(StatusText.Trim());
        if (!string.IsNullOrWhiteSpace(StatsText))
        {
            summary.AppendLine();
            summary.AppendLine($"{L("Stats", "统计")}:");
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
            ErrorText = L("Runtime metrics copied", "运行指标已复制");
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
        ErrorText = L("Profile summary copied", "配置摘要已复制");
    }

    private void CopyProfileConfig()
    {
        if (!HasSelectedProfileConfig() || SelectedProfile is null)
        {
            return;
        }

        CopyTextRequested?.Invoke(this, SelectedProfile.CoreConfigJson);
        ErrorText = L("Profile config copied", "配置 JSON 已复制");
    }

    private void CopyProfileIssues()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        CopyTextRequested?.Invoke(this, BuildSelectedProfileIssuesReport());
        ErrorText = L("Profile issues copied", "配置问题已复制");
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
        ErrorText = IsChinese ? $"启动配置: {SelectedProfile.Name}" : $"Startup profile: {SelectedProfile.Name}";
    }

    private void ClearStartupProfile()
    {
        Settings.AutoConnectProfileId = null;
        SaveSettings();
        NotifyStartupProfileChanged();
        ErrorText = L("Startup profile cleared", "启动配置已清除");
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
            ErrorText = IsChinese ? $"打开文件夹失败: {ex.Message}" : $"Open folder failed: {ex.Message}";
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
        ErrorText = L("Filtered logs copied", "已复制过滤后的日志");
    }

    private void CopySubscriptionStatus()
    {
        if (!HasSubscriptionStatusResult())
        {
            return;
        }

        CopyTextRequested?.Invoke(this, SubscriptionStatusText);
        ErrorText = L("Subscription result copied", "订阅结果已复制");
    }

    private void CopySubscriptionSource()
    {
        if (SelectedSubscription is null)
        {
            return;
        }

        CopyTextRequested?.Invoke(this, BuildSubscriptionSourceSummary(SelectedSubscription));
        ErrorText = L("Subscription source copied", "订阅来源已复制");
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
            RuntimeState.Running => T.Connected,
            RuntimeState.Degraded => T.Degraded,
            RuntimeState.Starting => T.Starting,
            RuntimeState.Stopping => T.Stopping,
            RuntimeState.Faulted => T.Faulted,
            RuntimeState.Recovering => T.Recovering,
            _ => T.Disconnected
        };
        ConnectionDetail = status is null
            ? T.SidecarNotRunning
            : IsChinese ? $"{status.Mode} 模式，已运行 {FormatDuration(status.UptimeSeconds)}" : $"{status.Mode} mode, uptime {FormatDuration(status.UptimeSeconds)}";
        ActiveProfileSummary = SelectedProfile is null
            ? "-"
            : $"{SelectedProfile.Name} ({SelectedProfile.Kind}, {SelectedProfile.Source})";
        ProxySummary = FormatProxyMode(SelectedProxyMode);
        CoreSummary = status is null ? T.CoreNotRunning : $"{status.Version} / {ShortCommit(status.Commit)}";
        RecentIssueSummary = FirstNonEmpty(ErrorText, status?.LastFatalError, SelectedProfile?.LastValidationError, "-");
        ValidationSummary = SelectedProfile?.LastValidationError is { Length: > 0 } validationError
            ? validationError
            : SelectedProfile?.LastValidatedAt is { } validatedAt
                ? $"{T.LastChecked} {validatedAt.LocalDateTime:g}"
                : T.NotValidated;
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
        StatusBarTrafficSummary = $"{FormatBytes(sent)} {T.Up} / {FormatBytes(received)} {T.Down}";
        StatusBarChannelSummary = avgRtt > 0
            ? $"{upChannels}/{totalChannels} {T.Up}, {avgRtt * 1000:0} ms RTT"
            : $"{upChannels}/{totalChannels} {T.Up}, {T.RttWaiting}";

        ReplaceCollection(OverviewMetrics,
        [
            new DashboardMetric { Label = T.Runtime, Value = ConnectionHeading, Detail = ConnectionDetail },
            new DashboardMetric { Label = T.Traffic, Value = $"{FormatBytes(sent)} {T.Up}", Detail = $"{FormatBytes(received)} {T.Down}" },
            new DashboardMetric { Label = T.Channels, Value = $"{upChannels}/{totalChannels} {T.Up}", Detail = avgRtt > 0 ? $"{avgRtt * 1000:0} ms {T.AvgRtt}" : T.WaitingForRtt },
            new DashboardMetric { Label = T.Listeners, Value = $"{runningListeners}/{totalListeners} {T.Running}", Detail = ListenerDetail(status) },
            new DashboardMetric { Label = T.Reconnects, Value = reconnects.ToString(CultureInfo.InvariantCulture), Detail = T.LanguageCode == "zh-CN" ? $"{activeStreams} 个活动流" : $"{activeStreams} active streams" },
            new DashboardMetric { Label = T.ProxyModeMetric, Value = ProxySummary, Detail = LocalProxySummary }
        ]);

        ReplaceCollection(ListenerRows, status?.Listeners.Select(x => new SummaryRow
        {
            Name = x.Protocol,
            Value = string.IsNullOrWhiteSpace(x.Actual) ? x.Configured : x.Actual,
            Detail = string.IsNullOrWhiteSpace(x.LastError) ? x.State : x.LastError
        }) ?? []);

        ReplaceCollection(ChannelRows, status?.Client?.Channels.Select(x => new SummaryRow
        {
            Name = $"{T.Channel} {x.Channel}",
            Value = x.Up ? T.Up : T.Down,
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

    private static SelectOption<T>? FindOption<T>(IEnumerable<SelectOption<T>> options, T value)
    {
        return options.FirstOrDefault(x => EqualityComparer<T>.Default.Equals(x.Value, value));
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
            SetCorePathStatus(T.LanguageCode == "zh-CN" ? "已自动检测" : "Auto-detected", resolved, "#065F46", "#D1FAE5");
            return;
        }

        SetCorePathStatus(
            T.LanguageCode == "zh-CN" ? "内核缺失" : "Core missing",
            T.LanguageCode == "zh-CN" ? "设置 x-tunnel.exe 完整路径，或放入预期的 build/output 文件夹。" : "Set a full path to x-tunnel.exe or place it in the expected build/output folder.",
            "#7F1D1D",
            "#FECACA");
    }

    private void SetCorePathStatusForPath(string path, bool configuredSource)
    {
        if (!File.Exists(path))
        {
            SetCorePathStatus(T.LanguageCode == "zh-CN" ? "路径缺失" : "Path missing", path, "#7F1D1D", "#FECACA");
            return;
        }

        if (!string.Equals(Path.GetFileName(path), "x-tunnel.exe", StringComparison.OrdinalIgnoreCase))
        {
            SetCorePathStatus(T.LanguageCode == "zh-CN" ? "检查程序" : "Check executable", path, "#92400E", "#FEF3C7");
            return;
        }

        SetCorePathStatus(
            configuredSource
                ? T.LanguageCode == "zh-CN" ? "已配置" : "Configured"
                : T.LanguageCode == "zh-CN" ? "已自动检测" : "Auto-detected",
            path,
            "#065F46",
            "#D1FAE5");
    }

    private void SetCorePathStatus(string status, string detail, string background, string foreground)
    {
        CorePathStatus = status;
        CorePathStatusDetail = detail;
        CorePathStatusBackground = background;
        CorePathStatusForeground = foreground;
    }

    private string FormatProxyMode(ProxyMode mode)
    {
        return mode switch
        {
            ProxyMode.System => T.ProxyModeSystem,
            ProxyMode.Pac => T.ProxyModePac,
            ProxyMode.Tun => T.ProxyModeTun,
            _ => T.ProxyModeOff
        };
    }

    private string ListenerDetail(CoreStatus? status)
    {
        if (status?.Listeners.Count > 0)
        {
            return string.Join(", ", status.Listeners.Select(x => string.IsNullOrWhiteSpace(x.Actual) ? x.Configured : x.Actual));
        }
        return T.NoListeners;
    }

    private string ShortCommit(string? commit)
    {
        if (string.IsNullOrWhiteSpace(commit))
        {
            return T.Unknown;
        }
        return commit.Length <= 8 ? commit : commit[..8];
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
    }

    private bool IsChinese => string.Equals(T.LanguageCode, "zh-CN", StringComparison.Ordinal);

    private string L(string english, string chinese) => IsChinese ? chinese : english;

    private string TimeStatus(string englishPrefix, string chinesePrefix)
    {
        return $"{L(englishPrefix, chinesePrefix)} {DateTimeOffset.Now.LocalDateTime:T}";
    }

    private string NoSubscriptionSelectedText => L("No subscription selected", "未选择订阅");
    private string NoSubscriptionsConfiguredText => L("No subscriptions configured", "未配置订阅");
    private string NetworkNotTestedText => L("Not tested", "未测试");
    private string TestingNetworkText => L("Testing network...", "正在测试网络...");
    private string TestingProfileEndpointText => L("Testing profile forward endpoint...", "正在测试配置转发端点...");
    private string DefaultNetworkTestDetailText => L("Run Test Network to check direct and proxy routes.", "运行网络测试以检查直连和代理路由。");
    private string StoppedText => L("Stopped", "已停止");

    private string LocalizeRouteName(string route)
    {
        if (!IsChinese)
        {
            return route;
        }

        return route switch
        {
            "Direct" => "直连",
            "Proxy" => "代理",
            "Forward TCP" => "转发 TCP",
            _ when route.Contains("proxy", StringComparison.OrdinalIgnoreCase) => route.Replace("Proxy", "代理", StringComparison.OrdinalIgnoreCase),
            _ => route
        };
    }

    private string LocalizeSubscriptionResult(string result)
    {
        if (!IsChinese || string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        if (string.Equals(result, "not saved", StringComparison.OrdinalIgnoreCase))
        {
            return "未保存";
        }
        if (string.Equals(result, "never", StringComparison.OrdinalIgnoreCase))
        {
            return "从未更新";
        }
        if (string.Equals(result, "saved", StringComparison.OrdinalIgnoreCase))
        {
            return "已保存";
        }
        if (string.Equals(result, "not modified", StringComparison.OrdinalIgnoreCase))
        {
            return "未修改";
        }
        if (result.StartsWith("fetched ", StringComparison.OrdinalIgnoreCase))
        {
            return result
                .Replace("fetched", "已获取", StringComparison.OrdinalIgnoreCase)
                .Replace("profile(s)", "个配置", StringComparison.OrdinalIgnoreCase);
        }
        if (result.StartsWith("failed:", StringComparison.OrdinalIgnoreCase))
        {
            return "失败:" + result["failed:".Length..];
        }
        if (result.StartsWith("added ", StringComparison.OrdinalIgnoreCase))
        {
            return result
                .Replace("added", "新增", StringComparison.OrdinalIgnoreCase)
                .Replace("updated", "更新", StringComparison.OrdinalIgnoreCase)
                .Replace("unchanged", "未变更", StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    private static string ClassifyAppMessageForeground(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "#E5E7EB";
        }
        if (ContainsAny(message, "failed", "failure", "invalid", "error", "missing", "not found", "blocked", "无效", "失败", "错误"))
        {
            return "#FCA5A5";
        }
        if (ContainsAny(message, "copied", "saved", "exported", "cleared", "refreshed", "complete", "updated", "imported", "deleted", "restored", "set from", "formatted", "applied", "已", "成功", "保存"))
        {
            return "#86EFAC";
        }
        return "#FDE68A";
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAnyText(string value, params string[] candidates)
    {
        return candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateFilteredLogText()
    {
        var filter = LogFilterText.Trim();
        var level = SelectedLogLevelFilter;
        if (!TryBuildLogRegex(filter, out var regex, out var regexError))
        {
            FilteredLogText = "";
            LogFilterSummary = IsChinese ? $"无效正则: {regexError}" : $"Invalid regex: {regexError}";
            SetLogFilterBadge(IsChinese ? "无效正则" : "Invalid regex", "#7F1D1D", "#FECACA");
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
            LogFilterSummary = IsChinese ? $"显示 {shown} 条日志" : $"Showing {shown} log line(s)";
            SetLogFilterBadge(IsChinese ? "全部日志" : "All logs", "#374151", "#E5E7EB");
            return;
        }

        var mode = regex is null
            ? IsChinese ? "文本" : "text"
            : IsChinese ? "正则" : "regex";
        LogFilterSummary = IsChinese
            ? $"显示 {shown}/{total} 条日志，{mode}过滤"
            : $"Showing {shown}/{total} log line(s), {mode} filter";
        if (shown == 0 && total > 0)
        {
            SetLogFilterBadge(IsChinese ? "无匹配" : "No matches", "#92400E", "#FEF3C7");
            return;
        }

        SetLogFilterBadge(
            regex is null
                ? IsChinese ? "文本过滤" : "Text filter"
                : IsChinese ? "正则过滤" : "Regex filter",
            "#1E3A8A",
            "#BFDBFE");
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
        ReplaceCollection(FilteredProfileRows, matches.Select(BuildProfileListRow));
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
        SyncSelectedProfileRow();
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
            || ProfileFieldContains(profile.EndpointTestDetail, filter)
            || ProfileFieldContains(LocalizeValidationState(profile), filter)
            || ProfileFieldContains(LocalizeValidationDetail(profile), filter)
            || ProfileFieldContains(LocalizeEndpointTestState(profile), filter)
            || ProfileFieldContains(LocalizeEndpointTestDetail(profile), filter);
    }

    private static bool ProfileFieldContains(string? value, string filter)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private ProfileListRow BuildProfileListRow(Profile profile)
    {
        return new ProfileListRow
        {
            Profile = profile,
            Name = profile.Name,
            ListSubtitle = profile.ListSubtitle,
            ValidationState = LocalizeValidationState(profile),
            ValidationDetail = LocalizeValidationDetail(profile),
            ValidationBadgeBackground = profile.ValidationBadgeBackground,
            ValidationBadgeForeground = profile.ValidationBadgeForeground,
            EndpointTestState = LocalizeEndpointTestState(profile),
            EndpointTestDetail = LocalizeEndpointTestDetail(profile),
            EndpointBadgeBackground = profile.EndpointBadgeBackground,
            EndpointBadgeForeground = profile.EndpointBadgeForeground
        };
    }

    private string LocalizeValidationState(Profile profile)
    {
        return profile.ValidationState switch
        {
            "Ready" => L("Ready", "就绪"),
            "Issue" => L("Issue", "问题"),
            "Unchecked" => L("Unchecked", "未检查"),
            _ => profile.ValidationState
        };
    }

    private string LocalizeValidationDetail(Profile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.LastValidationError))
        {
            return profile.LastValidationError;
        }

        return profile.LastValidatedAt.HasValue
            ? $"{L("Checked", "已检查")} {profile.LastValidatedAt.Value.LocalDateTime:g}"
            : T.NotValidated;
    }

    private string LocalizeEndpointTestState(Profile profile)
    {
        if (!profile.LastEndpointTestAt.HasValue)
        {
            return L("TCP ?", "TCP ?");
        }

        return string.IsNullOrWhiteSpace(profile.LastEndpointTestError)
            ? $"TCP {profile.LastEndpointTestDurationMs ?? 0}ms"
            : L("TCP Fail", "TCP 失败");
    }

    private string LocalizeEndpointTestDetail(Profile profile)
    {
        if (!profile.LastEndpointTestAt.HasValue)
        {
            return L("Endpoint not tested", "端点未测试");
        }
        if (!string.IsNullOrWhiteSpace(profile.LastEndpointTestError))
        {
            return IsChinese
                ? $"端点失败: {profile.LastEndpointTestError}"
                : $"Endpoint failed: {profile.LastEndpointTestError}";
        }

        var target = string.IsNullOrWhiteSpace(profile.LastEndpointTestTarget)
            ? L("forward endpoint", "转发端点")
            : profile.LastEndpointTestTarget;
        return IsChinese
            ? $"端点正常 {profile.LastEndpointTestDurationMs ?? 0}ms -> {target}"
            : $"Endpoint ok {profile.LastEndpointTestDurationMs ?? 0}ms -> {target}";
    }

    private void SyncSelectedProfileRow()
    {
        _syncingProfileRowSelection = true;
        try
        {
            SelectedProfileRow = SelectedProfile is null
                ? null
                : FilteredProfileRows.FirstOrDefault(x => x.Profile.Id == SelectedProfile.Id);
        }
        finally
        {
            _syncingProfileRowSelection = false;
        }
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
        ProfileSummaryText = IsChinese
            ? $"配置 {FilteredProfiles.Count}/{Profiles.Count} 可见，{ready} 就绪，{issues} 问题，{endpointOk} 端点正常"
            : $"Profiles {FilteredProfiles.Count}/{Profiles.Count} visible, {ready} ready, {issues} issues, {endpointOk} endpoint ok";
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
        SetDirectRoute(L("Direct: not tested", "直连：未测试"), L("Run Test Network", "运行网络测试"), RouteVisual.Neutral);
        SetProxyRoute(L("Proxy: not tested", "代理：未测试"), L("Uses selected profile local proxy", "使用选中配置的本地代理"), RouteVisual.Neutral);
    }

    private void SetNetworkRouteReady(string target)
    {
        var detail = string.IsNullOrWhiteSpace(target) ? L("Enter an http or https URL", "输入 http 或 https URL") : target;
        SetDirectRoute(L("Direct: ready", "直连：就绪"), detail, RouteVisual.Neutral);
        SetProxyRoute(L("Proxy: ready", "代理：就绪"), L("Uses selected profile local proxy", "使用选中配置的本地代理"), RouteVisual.Neutral);
    }

    private void SetNetworkRouteInvalid(string detail)
    {
        SetDirectRoute(L("Direct: invalid target", "直连：目标无效"), detail, RouteVisual.Failed);
        SetProxyRoute(L("Proxy: invalid target", "代理：目标无效"), detail, RouteVisual.Failed);
    }

    private void SetNetworkRouteTesting(Uri target, LocalProxyEndpoints? endpoints, string? endpointError)
    {
        SetDirectRoute(L("Direct: testing", "直连：测试中"), target.ToString(), RouteVisual.Busy);
        if (endpoints is null)
        {
            SetProxyRoute(L("Proxy: skipped", "代理：已跳过"), FirstNonEmpty(endpointError, L("No selected proxy endpoint", "没有选中的代理端点")), RouteVisual.Warning);
            return;
        }

        SetProxyRoute(L("Proxy: testing", "代理：测试中"), BuildProxyEndpointSummary(endpoints), RouteVisual.Busy);
    }

    private void SetNetworkRouteFailed(string detail)
    {
        SetDirectRoute(L("Direct: failed", "直连：失败"), detail, RouteVisual.Failed);
        SetProxyRoute(L("Proxy: failed", "代理：失败"), detail, RouteVisual.Failed);
    }

    private void UpdateNetworkRouteStatus(IEnumerable<NetworkTestResult> results, string? note)
    {
        var tested = results.ToList();
        var direct = tested.FirstOrDefault(x => string.Equals(x.Route, "Direct", StringComparison.OrdinalIgnoreCase));
        var proxy = tested.FirstOrDefault(x => !string.Equals(x.Route, "Direct", StringComparison.OrdinalIgnoreCase));

        if (direct is null)
        {
            SetDirectRoute(L("Direct: skipped", "直连：已跳过"), L("No direct route result", "没有直连路由结果"), RouteVisual.Warning);
        }
        else
        {
            SetDirectRoute(BuildRouteStatus(direct), BuildRouteDetail(direct), direct.Success ? RouteVisual.Ok : RouteVisual.Failed);
        }

        if (proxy is null)
        {
            SetProxyRoute(L("Proxy: skipped", "代理：已跳过"), FirstNonEmpty(note, L("No local proxy endpoint available", "没有可用的本地代理端点")), RouteVisual.Warning);
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
        SetProfileEndpointRoute(L("Forward TCP: not tested", "转发 TCP：未测试"), L("Uses selected profile forward endpoint", "使用选中配置的转发端点"), RouteVisual.Neutral);
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

    private string BuildRouteStatus(NetworkTestResult result)
    {
        var status = result.Success ? L("ok", "正常") : L("failed", "失败");
        return $"{LocalizeRouteName(result.Route)}: {status} {result.DurationMs}ms";
    }

    private string BuildRouteDetail(NetworkTestResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            return result.Error;
        }

        var status = result.StatusCode.HasValue ? $"{L("status", "状态")} {result.StatusCode}; " : "";
        var proxy = string.IsNullOrWhiteSpace(result.Proxy) ? "" : $" {L("via", "经由")} {result.Proxy}";
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

    private string FormatNetworkTestResults(IEnumerable<NetworkTestResult> results, string? note)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(note))
        {
            lines.Add(note);
        }
        foreach (var result in results)
        {
            var status = result.Success ? L("ok", "正常") : L("failed", "失败");
            var code = result.StatusCode.HasValue ? $" {L("status", "状态")}={result.StatusCode}" : "";
            var proxy = string.IsNullOrWhiteSpace(result.Proxy) ? "" : $" {L("proxy", "代理")}={result.Proxy}";
            var error = string.IsNullOrWhiteSpace(result.Error) ? "" : $" {L("error", "错误")}={result.Error}";
            lines.Add($"{LocalizeRouteName(result.Route)}: {status}{code} {L("time", "耗时")}={result.DurationMs}ms {L("target", "目标")}={result.Target}{proxy}{error}");
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
            NetworkTestSummary = L("Direct + proxy ok", "直连和代理正常");
            NetworkTestDetail = IsChinese
                ? $"直连 {FormatResultDuration(direct)}，{LocalizeRouteName(proxy.Route)} {FormatResultDuration(proxy)}"
                : $"Direct {FormatResultDuration(direct)}, {proxy.Route} {FormatResultDuration(proxy)}";
            return;
        }

        if (direct?.Success == true && proxy is null)
        {
            NetworkTestSummary = L("Direct ok", "直连正常");
            NetworkTestDetail = AppendNote(
                IsChinese
                    ? $"直连 {FormatResultDuration(direct)}；代理路由已跳过"
                    : $"Direct {FormatResultDuration(direct)}; proxy route skipped",
                note);
            return;
        }

        if (direct?.Success == true)
        {
            NetworkTestSummary = L("Direct ok, proxy failed", "直连正常，代理失败");
            NetworkTestDetail = AppendNote(
                IsChinese
                    ? $"直连 {FormatResultDuration(direct)}；{LocalizeRouteName(proxy?.Route ?? "proxy")} 失败"
                    : $"Direct {FormatResultDuration(direct)}; {proxy?.Route ?? "proxy"} failed",
                note);
            return;
        }

        if (proxy?.Success == true)
        {
            NetworkTestSummary = L("Proxy ok, direct failed", "代理正常，直连失败");
            NetworkTestDetail = IsChinese
                ? $"{LocalizeRouteName(proxy.Route)} {FormatResultDuration(proxy)}；直连失败"
                : $"{proxy.Route} {FormatResultDuration(proxy)}; direct failed";
            return;
        }

        NetworkTestSummary = L("Network failed", "网络失败");
        NetworkTestDetail = AppendNote(FirstNonEmpty(direct?.Error, proxy?.Error, L("No route succeeded", "没有路由成功")), note);
    }

    private void UpdateNetworkTestBadge()
    {
        if (ContainsAny(NetworkTestSummary, "failed", "失败")
            && ContainsAny(NetworkTestSummary, "ok", "正常"))
        {
            NetworkTestBadgeBackground = "#92400E";
            NetworkTestBadgeForeground = "#FEF3C7";
            return;
        }

        if (ContainsAny(NetworkTestSummary, "ok", "正常"))
        {
            NetworkTestBadgeBackground = "#065F46";
            NetworkTestBadgeForeground = "#D1FAE5";
            return;
        }

        if (ContainsAny(NetworkTestSummary, "failed", "invalid", "失败", "无效"))
        {
            NetworkTestBadgeBackground = "#7F1D1D";
            NetworkTestBadgeForeground = "#FECACA";
            return;
        }

        if (ContainsAny(NetworkTestSummary, "testing", "ready", "正在", "准备", "就绪"))
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
            SubscriptionStatusText = L("Subscription name is required", "订阅名称必填");
            ErrorText = SubscriptionStatusText;
            return false;
        }
        if (!Uri.TryCreate(subscription.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            SubscriptionStatusText = L("Subscription URL must be an absolute http or https URL", "订阅 URL 必须是绝对 http 或 https URL");
            ErrorText = SubscriptionStatusText;
            return false;
        }
        if (subscription.UpdateIntervalMinutes <= 0)
        {
            SubscriptionStatusText = L("Update interval must be greater than zero", "更新间隔必须大于 0");
            ErrorText = SubscriptionStatusText;
            return false;
        }
        return true;
    }

    private string BuildSubscriptionStatus(Subscription subscription)
    {
        var updated = subscription.LastUpdatedAt?.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) ?? "-";
        return string.Join(Environment.NewLine, new[]
        {
            $"{L("Last result", "上次结果")}: {LocalizeSubscriptionResult(subscription.LastResult)}",
            $"{L("Updated", "更新时间")}: {updated}",
            $"{L("Cache", "缓存")}: etag={subscription.ETag ?? "-"} modified={subscription.LastModified ?? "-"}"
        });
    }

    private string BuildSubscriptionSourceSummary(Subscription subscription)
    {
        var updated = subscription.LastUpdatedAt?.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) ?? "-";
        return string.Join(Environment.NewLine, new[]
        {
            $"{L("Subscription", "订阅")}: {subscription.DisplayName}",
            $"URL: {subscription.Url}",
            $"{L("Interval", "间隔")}: {subscription.UpdateIntervalMinutes} {L("minute(s)", "分钟")}",
            $"{L("Trust", "信任策略")}: {subscription.TrustPolicy}",
            $"{L("Last result", "上次结果")}: {LocalizeSubscriptionResult(subscription.LastResult)}",
            $"{L("Updated", "更新时间")}: {updated}"
        });
    }

    private void UpdateFilteredSubscriptions(Guid? preferredSubscriptionId = null)
    {
        var selectedId = preferredSubscriptionId ?? SelectedSubscription?.Id;
        var matches = ApplySubscriptionSort(Subscriptions.Where(MatchesSubscriptionSearch)).ToList();
        ReplaceCollection(FilteredSubscriptions, matches);
        ReplaceCollection(FilteredSubscriptionRows, matches.Select(BuildSubscriptionListRow));
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
        SyncSelectedSubscriptionRow();
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
            || SubscriptionFieldContains(subscription.TrustPolicy, filter)
            || SubscriptionFieldContains(LocalizeSubscriptionResult(subscription.LastResult), filter)
            || SubscriptionFieldContains(LocalizeSubscriptionUpdateState(subscription), filter)
            || SubscriptionFieldContains(LocalizeSubscriptionUpdateDetail(subscription), filter);
    }

    private static bool SubscriptionFieldContains(string? value, string filter)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private SubscriptionListRow BuildSubscriptionListRow(Subscription subscription)
    {
        return new SubscriptionListRow
        {
            Subscription = subscription,
            DisplayName = subscription.DisplayName,
            ListSubtitle = $"{L("Last result", "上次结果")}: {LocalizeSubscriptionResult(subscription.LastResult)}",
            UpdateState = LocalizeSubscriptionUpdateState(subscription),
            UpdateDetail = LocalizeSubscriptionUpdateDetail(subscription),
            UpdateBadgeBackground = subscription.UpdateBadgeBackground,
            UpdateBadgeForeground = subscription.UpdateBadgeForeground
        };
    }

    private string LocalizeSubscriptionUpdateState(Subscription subscription)
    {
        return subscription.UpdateState switch
        {
            "Failed" => L("Failed", "失败"),
            "Updated" => L("Updated", "已更新"),
            "New" => L("New", "新建"),
            _ => subscription.UpdateState
        };
    }

    private string LocalizeSubscriptionUpdateDetail(Subscription subscription)
    {
        return subscription.LastUpdatedAt.HasValue
            ? $"{L("Updated", "已更新")} {subscription.LastUpdatedAt.Value.LocalDateTime:g}"
            : L("Not updated", "未更新");
    }

    private void SyncSelectedSubscriptionRow()
    {
        _syncingSubscriptionRowSelection = true;
        try
        {
            SelectedSubscriptionRow = SelectedSubscription is null
                ? null
                : FilteredSubscriptionRows.FirstOrDefault(x => x.Subscription.Id == SelectedSubscription.Id);
        }
        finally
        {
            _syncingSubscriptionRowSelection = false;
        }
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
            SubscriptionSummaryText = IsChinese
                ? "订阅 0/0 可见，0 已更新，0 失败"
                : "Subscriptions 0/0 visible, 0 updated, 0 failed";
            return;
        }

        var updated = Subscriptions.Count(x => x.LastUpdatedAt.HasValue);
        var failed = Subscriptions.Count(x => x.LastResult.StartsWith("failed:", StringComparison.OrdinalIgnoreCase));
        SubscriptionSummaryText = IsChinese
            ? $"订阅 {FilteredSubscriptions.Count}/{Subscriptions.Count} 可见，{updated} 已更新，{failed} 失败"
            : $"Subscriptions {FilteredSubscriptions.Count}/{Subscriptions.Count} visible, {updated} updated, {failed} failed";
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
            SetDiagnosticsPortStatus(L("Ports: no profile", "端口：未选择配置"), L("Select a profile before running diagnostics.", "运行诊断前请选择配置。"), "#92400E", "#FEF3C7");
            return;
        }

        if (report.PortChecks.Count == 0)
        {
            var warning = report.Warnings.FirstOrDefault(x => x.Contains("port check failed", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(warning))
            {
                SetDiagnosticsPortStatus(L("Ports: check failed", "端口：检查失败"), warning, "#7F1D1D", "#FECACA");
                return;
            }

            SetDiagnosticsPortStatus(L("Ports: none", "端口：无监听"), L("Selected profile has no local listen endpoint.", "选中配置没有本地监听端点。"), "#374151", "#E5E7EB");
            return;
        }

        var detail = string.Join("; ", report.PortChecks.Select(FormatPortCheck));
        if (report.PortChecks.Any(x => !x.Available))
        {
            SetDiagnosticsPortStatus(L("Ports: blocked", "端口：被占用"), detail, "#7F1D1D", "#FECACA");
            return;
        }

        SetDiagnosticsPortStatus(L("Ports: available", "端口：可用"), detail, "#065F46", "#D1FAE5");
    }

    private void SetDiagnosticsPortStatus(string status, string detail, string background, string foreground)
    {
        DiagnosticsPortStatus = status;
        DiagnosticsPortDetail = detail;
        DiagnosticsPortBackground = background;
        DiagnosticsPortForeground = foreground;
    }

    private string FormatPortCheck(PortCheckResult result)
    {
        return result.Available
            ? $"{result.Address} {L("ok", "正常")}"
            : $"{result.Address} {L("blocked", "被占用")} ({result.Error})";
    }

    private string BuildDiagnosticsSummary(DiagnosticReport report)
    {
        var lines = new List<string>
        {
            $"{L("Created", "创建时间")}: {report.CreatedAt.LocalDateTime:g}",
            $"OS: {report.OsVersion} / {report.Architecture}",
            $"GUI: {report.GuiVersion}",
            $"{T.Profile}: {report.ActiveProfile?.Name ?? "-"}",
            $"{T.Core}: {report.Status?.Version ?? "-"} {report.Status?.Mode ?? ""}".Trim(),
            $"{T.Proxy}: enable={report.CurrentProxy?.ProxyEnable ?? 0} server={report.CurrentProxy?.ProxyServer ?? "-"} pac={report.CurrentProxy?.AutoConfigUrl ?? "-"}"
        };
        if (report.PortChecks.Count > 0)
        {
            lines.Add(L("Ports", "端口") + ": " + string.Join("; ", report.PortChecks.Select(FormatPortCheck)));
        }
        if (report.Warnings.Count > 0)
        {
            lines.Add(L("Warnings", "警告") + ": " + string.Join("; ", report.Warnings));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void ApplyLanguage(string? language, bool refresh = true)
    {
        Settings.Language = AppText.NormalizeLanguage(language);
        var culture = AppText.CultureFor(Settings.Language);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        T = AppText.For(Settings.Language);
        RefreshOptionLists();
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(RuntimeStateText));

        if (refresh)
        {
            RefreshLocalizedUi();
        }
    }

    private void RefreshLocalizedUi()
    {
        OnPropertyChanged(nameof(StartupProfileSummary));
        RefreshTransientLocalizedUi();
        UpdateFilteredProfiles(SelectedProfile?.Id);
        UpdateFilteredSubscriptions(SelectedSubscription?.Id);
        UpdateFilteredLogText();
        RefreshOverview(_supervisor.CurrentStatus, _supervisor.CurrentStats);
    }

    private void RefreshTransientLocalizedUi()
    {
        if (IsAnyText(StatusText, "Stopped", "已停止"))
        {
            StatusText = StoppedText;
        }

        if (IsAnyText(NetworkTestText, "Not tested", "未测试"))
        {
            NetworkTestText = NetworkNotTestedText;
            NetworkTestSummary = NetworkNotTestedText;
            NetworkTestDetail = DefaultNetworkTestDetailText;
            NetworkTestLastRunText = L("Network test not run", "网络测试未运行");
            SetNetworkRouteNotTested();
        }

        if (IsAnyText(ProfileEndpointTestText, "Not tested", "未测试"))
        {
            ProfileEndpointTestText = NetworkNotTestedText;
            ProfileEndpointTestLastRunText = L("Forward test not run", "转发测试未运行");
            SetProfileEndpointRouteNotTested();
        }

        if (IsAnyText(SubscriptionStatusText, "No subscriptions configured", "未配置订阅"))
        {
            SubscriptionStatusText = NoSubscriptionsConfiguredText;
        }
        else if (IsAnyText(SubscriptionStatusText, "No subscription selected", "未选择订阅"))
        {
            SubscriptionStatusText = NoSubscriptionSelectedText;
        }
        else if (SelectedSubscription is { } subscription && ContainsAny(SubscriptionStatusText, "Last result", "上次结果"))
        {
            SubscriptionStatusText = BuildSubscriptionStatus(subscription);
        }

        if (IsAnyText(DiagnosticsLastRunText, "Diagnostics not run", "诊断未运行"))
        {
            DiagnosticsLastRunText = L("Diagnostics not run", "诊断未运行");
        }

        if (IsAnyText(DiagnosticsPortStatus, "Ports: not checked", "端口：未检查"))
        {
            DiagnosticsPortStatus = L("Ports: not checked", "端口：未检查");
            DiagnosticsPortDetail = L("Run checks to inspect selected profile listen ports.", "运行检查以检查选中配置的监听端口。");
        }

        RefreshCorePathStatusLanguage();
    }

    private void RefreshCorePathStatusLanguage()
    {
        if (IsAnyText(CorePathStatus, "Version OK", "版本正常"))
        {
            CorePathStatus = L("Version OK", "版本正常");
        }
        else if (IsAnyText(CorePathStatus, "Version failed", "版本检查失败"))
        {
            CorePathStatus = L("Version failed", "版本检查失败");
        }
        else if (IsAnyText(CorePathStatus, "Core missing", "内核缺失"))
        {
            CorePathStatus = L("Core missing", "内核缺失");
            if (ContainsAny(CorePathStatusDetail, "Set or auto-detect x-tunnel.exe before checking the version.", "检查版本前请设置或自动检测 x-tunnel.exe。"))
            {
                CorePathStatusDetail = L("Set or auto-detect x-tunnel.exe before checking the version.", "检查版本前请设置或自动检测 x-tunnel.exe。");
            }
        }
        else if (IsAnyText(CorePathStatus, "Configured", "已配置"))
        {
            CorePathStatus = L("Configured", "已配置");
        }
        else if (IsAnyText(CorePathStatus, "Auto-detected", "已自动检测"))
        {
            CorePathStatus = L("Auto-detected", "已自动检测");
        }
        else if (IsAnyText(CorePathStatus, "Path missing", "路径缺失"))
        {
            CorePathStatus = L("Path missing", "路径缺失");
        }
        else if (IsAnyText(CorePathStatus, "Check executable", "检查程序"))
        {
            CorePathStatus = L("Check executable", "检查程序");
        }
    }

    private void RefreshOptionLists()
    {
        ReplaceCollection(ProxyModeOptions,
        [
            new SelectOption<ProxyMode>(ProxyMode.Off, T.ProxyModeOff),
            new SelectOption<ProxyMode>(ProxyMode.System, T.ProxyModeSystem),
            new SelectOption<ProxyMode>(ProxyMode.Pac, T.ProxyModePac),
            new SelectOption<ProxyMode>(ProxyMode.Tun, T.ProxyModeTun)
        ]);
        ReplaceCollection(ThemeOptions,
        [
            new SelectOption<string>("system", T.ThemeSystem),
            new SelectOption<string>("light", T.ThemeLight),
            new SelectOption<string>("dark", T.ThemeDark)
        ]);
        ReplaceCollection(UpdateChannels,
        [
            new SelectOption<string>("stable", T.UpdateChannelStable),
            new SelectOption<string>("beta", T.UpdateChannelBeta),
            new SelectOption<string>("disabled", T.UpdateChannelDisabled)
        ]);
        ReplaceCollection(LogLevelFilters,
        [
            new SelectOption<string>("All", T.FilterAll),
            new SelectOption<string>("debug", T.FilterDebug),
            new SelectOption<string>("info", T.FilterInfo),
            new SelectOption<string>("warn", T.FilterWarn),
            new SelectOption<string>("error", T.FilterError)
        ]);
        ReplaceCollection(ProfileSortOptions,
        [
            new SelectOption<string>("Saved", T.SortSaved),
            new SelectOption<string>("Name", T.SortName),
            new SelectOption<string>("Endpoint", T.SortEndpoint)
        ]);
        ReplaceCollection(SubscriptionSortOptions,
        [
            new SelectOption<string>("Saved", T.SortSaved),
            new SelectOption<string>("Name", T.SortName),
            new SelectOption<string>("Updated", T.SortUpdated),
            new SelectOption<string>("Status", T.SortStatus)
        ]);

        OnPropertyChanged(nameof(SelectedProxyModeOption));
        OnPropertyChanged(nameof(SelectedThemeOption));
        OnPropertyChanged(nameof(SelectedUpdateChannelOption));
        OnPropertyChanged(nameof(SelectedLogLevelFilterOption));
        OnPropertyChanged(nameof(SelectedProfileSortOption));
        OnPropertyChanged(nameof(SelectedSubscriptionSortOption));
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
                    Message = IsChinese ? $"{result.Address} 可用。" : $"{result.Address} is available."
                }
                : new ProfileIssue
                {
                    Field = "listen",
                    Severity = "error",
                    Message = IsChinese
                        ? $"{result.Address} 被占用或无法绑定: {result.Error}"
                        : $"{result.Address} is occupied or cannot bind: {result.Error}"
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
            && !IsAnyText(NetworkTestText, "Not tested", "未测试", "Testing network...", "正在测试网络...");
    }

    private bool HasProfileEndpointTestResult()
    {
        return !string.IsNullOrWhiteSpace(ProfileEndpointTestText)
            && !IsAnyText(ProfileEndpointTestText, "Not tested", "未测试", "Testing profile forward endpoint...", "正在测试配置转发端点...", "No selected profile", "未选择配置");
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
            && !IsAnyText(StatusText, "Stopped", "已停止");
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
            && !IsAnyText(SubscriptionStatusText, "No subscriptions configured", "未配置订阅", "No subscription selected", "未选择订阅")
            && !SubscriptionStatusText.StartsWith("Fetching subscription", StringComparison.OrdinalIgnoreCase)
            && !SubscriptionStatusText.StartsWith("正在获取订阅", StringComparison.OrdinalIgnoreCase)
            && !SubscriptionStatusText.StartsWith("Updating ", StringComparison.OrdinalIgnoreCase)
            && !SubscriptionStatusText.StartsWith("正在更新 ", StringComparison.OrdinalIgnoreCase);
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
