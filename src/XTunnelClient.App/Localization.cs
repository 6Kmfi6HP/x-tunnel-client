using System.Globalization;
using System.Runtime.CompilerServices;

namespace XTunnelClient.App;

public sealed record LanguageOption(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class AppText
{
    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(AppTitle)] = "x-tunnel Client",
        [nameof(Connect)] = "Connect",
        [nameof(Disconnect)] = "Disconnect",
        [nameof(Restart)] = "Restart",
        [nameof(Diagnostics)] = "Diagnostics",
        [nameof(Traffic)] = "Traffic",
        [nameof(Channels)] = "Channels",
        [nameof(Core)] = "Core",
        [nameof(Issue)] = "Issue",
        [nameof(Overview)] = "Overview",
        [nameof(Profile)] = "Profile",
        [nameof(Profiles)] = "Profiles",
        [nameof(Subscriptions)] = "Subscriptions",
        [nameof(Logs)] = "Logs",
        [nameof(Settings)] = "Settings",
        [nameof(Proxy)] = "Proxy",
        [nameof(Local)] = "Local",
        [nameof(Network)] = "Network",
        [nameof(Validation)] = "Validation",
        [nameof(Control)] = "Control",
        [nameof(ProxyMode)] = "Proxy mode",
        [nameof(ProxyModeOff)] = "Off",
        [nameof(ProxyModeSystem)] = "System",
        [nameof(ProxyModePac)] = "PAC",
        [nameof(CorePathPlaceholder)] = "Auto-detect or full path to x-tunnel.exe",
        [nameof(Save)] = "Save",
        [nameof(RestoreProxy)] = "Restore Proxy",
        [nameof(ProxyAddress)] = "Proxy Addr",
        [nameof(CopyStatus)] = "Copy Status",
        [nameof(Open)] = "Open",
        [nameof(Listeners)] = "Listeners",
        [nameof(RecentLogs)] = "Recent Logs",
        [nameof(Copy)] = "Copy",
        [nameof(RuntimeDetails)] = "Runtime Details",
        [nameof(CopyMetrics)] = "Copy Metrics",
        [nameof(SearchProfiles)] = "Search profiles",
        [nameof(SearchSubscriptions)] = "Search subscriptions",
        [nameof(Clear)] = "Clear",
        [nameof(Sort)] = "Sort",
        [nameof(New)] = "New",
        [nameof(Delete)] = "Delete",
        [nameof(TestSelected)] = "Test Selected",
        [nameof(TestVisible)] = "Test Visible",
        [nameof(TestFastest)] = "Test + Fastest",
        [nameof(Fastest)] = "Fastest",
        [nameof(ClearTests)] = "Clear Tests",
        [nameof(Startup)] = "Startup",
        [nameof(ClearStartup)] = "Clear Startup",
        [nameof(ProfileNamePlaceholder)] = "Profile name",
        [nameof(SourcePlaceholder)] = "source",
        [nameof(Fallback)] = "fallback",
        [nameof(SecretRefPlaceholder)] = "secret ref, e.g. profile-token",
        [nameof(ProfileSecretPlaceholder)] = "profile secret saved with DPAPI",
        [nameof(ApplyForm)] = "Apply Form",
        [nameof(Validate)] = "Validate",
        [nameof(Format)] = "Format",
        [nameof(ImportFile)] = "Import File",
        [nameof(ImportClipboard)] = "Import Clipboard",
        [nameof(Export)] = "Export",
        [nameof(CopySummary)] = "Copy Summary",
        [nameof(CopyConfig)] = "Copy Config",
        [nameof(CopyIssues)] = "Copy Issues",
        [nameof(ProfileChecks)] = "Profile Checks",
        [nameof(Update)] = "Update",
        [nameof(UpdateAll)] = "Update All",
        [nameof(Name)] = "Name",
        [nameof(Url)] = "URL",
        [nameof(UpdateIntervalMinutes)] = "Update interval minutes",
        [nameof(TrustPolicy)] = "Trust policy",
        [nameof(UpdateNow)] = "Update Now",
        [nameof(SaveSubscription)] = "Save Subscription",
        [nameof(CopySource)] = "Copy Source",
        [nameof(CopyResult)] = "Copy Result",
        [nameof(RefreshDiagnostics)] = "Refresh Diagnostics",
        [nameof(ExportDiagnostics)] = "Export Diagnostics",
        [nameof(FilterLogsPlaceholder)] = "Filter logs, /regex/",
        [nameof(ClearFilters)] = "Clear Filters",
        [nameof(CopyLogs)] = "Copy Logs",
        [nameof(RunAll)] = "Run All",
        [nameof(RunChecks)] = "Run Checks",
        [nameof(CopyReport)] = "Copy Report",
        [nameof(ExportZip)] = "Export Zip",
        [nameof(OpenLogs)] = "Open Logs",
        [nameof(CopyPorts)] = "Copy Ports",
        [nameof(Target)] = "Target",
        [nameof(TestUrl)] = "Test URL",
        [nameof(TestNetwork)] = "Test Network",
        [nameof(TestForward)] = "Test Forward",
        [nameof(CopyForward)] = "Copy Forward",
        [nameof(ClearForward)] = "Clear Forward",
        [nameof(LaunchAtLogin)] = "Launch at login",
        [nameof(StartMinimized)] = "Start minimized",
        [nameof(AutoConnectSelectedProfile)] = "Auto-connect selected profile",
        [nameof(StartupDelaySeconds)] = "Startup delay seconds",
        [nameof(Theme)] = "Theme",
        [nameof(Language)] = "Language",
        [nameof(PacBypassRules)] = "PAC bypass rules",
        [nameof(LogRetentionDays)] = "Log retention days",
        [nameof(UpdateChannel)] = "Update channel",
        [nameof(CoreExecutable)] = "Core executable",
        [nameof(UseDetected)] = "Use Detected",
        [nameof(CheckVersion)] = "Check Version",
        [nameof(CopyPath)] = "Copy Path",
        [nameof(LocalFolders)] = "Local folders",
        [nameof(CopyFolders)] = "Copy Folders",
        [nameof(AppData)] = "App Data",
        [nameof(Runtime)] = "Runtime",
        [nameof(SaveSettings)] = "Save Settings",
        [nameof(OpenDashboard)] = "Open Dashboard",
        [nameof(CopyProxyAddress)] = "Copy Proxy Address",
        [nameof(UpdateSubscription)] = "Update Subscription",
        [nameof(OpenLogsFolder)] = "Open Logs Folder",
        [nameof(Quit)] = "Quit",
        [nameof(ImportXTunnelProfile)] = "Import x-tunnel profile",
        [nameof(JsonFileType)] = "JSON",
        [nameof(ClipboardProfile)] = "Clipboard profile",
        [nameof(ExportProfile)] = "Export profile",
        [nameof(Connected)] = "Connected",
        [nameof(Degraded)] = "Degraded",
        [nameof(Starting)] = "Starting",
        [nameof(Stopping)] = "Stopping",
        [nameof(Faulted)] = "Faulted",
        [nameof(Recovering)] = "Recovering",
        [nameof(Disconnected)] = "Disconnected",
        [nameof(SidecarNotRunning)] = "Sidecar is not running.",
        [nameof(CoreNotRunning)] = "Core not running",
        [nameof(NotValidated)] = "Not validated",
        [nameof(LastChecked)] = "Last checked",
        [nameof(Up)] = "up",
        [nameof(Down)] = "down",
        [nameof(RttWaiting)] = "RTT waiting",
        [nameof(WaitingForRtt)] = "waiting for RTT",
        [nameof(AvgRtt)] = "avg RTT",
        [nameof(Reconnects)] = "Reconnects",
        [nameof(ProxyModeMetric)] = "Proxy Mode",
        [nameof(NoListeners)] = "no listeners",
        [nameof(Unknown)] = "unknown",
        [nameof(Running)] = "running",
        [nameof(Channel)] = "Channel",
        [nameof(SettingsSaved)] = "Settings saved",
        [nameof(LanguageRestartNotRequired)] = "Language switches immediately and is saved for the next launch.",
    };

    private static readonly IReadOnlyDictionary<string, string> Chinese = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(AppTitle)] = "x-tunnel 客户端",
        [nameof(Connect)] = "连接",
        [nameof(Disconnect)] = "断开",
        [nameof(Restart)] = "重启",
        [nameof(Diagnostics)] = "诊断",
        [nameof(Traffic)] = "流量",
        [nameof(Channels)] = "通道",
        [nameof(Core)] = "内核",
        [nameof(Issue)] = "问题",
        [nameof(Overview)] = "概览",
        [nameof(Profile)] = "配置",
        [nameof(Profiles)] = "配置",
        [nameof(Subscriptions)] = "订阅",
        [nameof(Logs)] = "日志",
        [nameof(Settings)] = "设置",
        [nameof(Proxy)] = "代理",
        [nameof(Local)] = "本地",
        [nameof(Network)] = "网络",
        [nameof(Validation)] = "校验",
        [nameof(Control)] = "控制",
        [nameof(ProxyMode)] = "代理模式",
        [nameof(ProxyModeOff)] = "关闭",
        [nameof(ProxyModeSystem)] = "系统",
        [nameof(ProxyModePac)] = "PAC",
        [nameof(CorePathPlaceholder)] = "自动检测或填写 x-tunnel.exe 完整路径",
        [nameof(Save)] = "保存",
        [nameof(RestoreProxy)] = "恢复代理",
        [nameof(ProxyAddress)] = "代理地址",
        [nameof(CopyStatus)] = "复制状态",
        [nameof(Open)] = "打开",
        [nameof(Listeners)] = "监听",
        [nameof(RecentLogs)] = "最近日志",
        [nameof(Copy)] = "复制",
        [nameof(RuntimeDetails)] = "运行详情",
        [nameof(CopyMetrics)] = "复制指标",
        [nameof(SearchProfiles)] = "搜索配置",
        [nameof(SearchSubscriptions)] = "搜索订阅",
        [nameof(Clear)] = "清除",
        [nameof(Sort)] = "排序",
        [nameof(New)] = "新建",
        [nameof(Delete)] = "删除",
        [nameof(TestSelected)] = "测试选中",
        [nameof(TestVisible)] = "测试可见",
        [nameof(TestFastest)] = "测试并选最快",
        [nameof(Fastest)] = "最快",
        [nameof(ClearTests)] = "清除测试",
        [nameof(Startup)] = "启动",
        [nameof(ClearStartup)] = "清除启动项",
        [nameof(ProfileNamePlaceholder)] = "配置名称",
        [nameof(SourcePlaceholder)] = "来源",
        [nameof(Fallback)] = "回退",
        [nameof(SecretRefPlaceholder)] = "密钥引用，例如 profile-token",
        [nameof(ProfileSecretPlaceholder)] = "使用 DPAPI 保存的配置密钥",
        [nameof(ApplyForm)] = "应用表单",
        [nameof(Validate)] = "校验",
        [nameof(Format)] = "格式化",
        [nameof(ImportFile)] = "导入文件",
        [nameof(ImportClipboard)] = "从剪贴板导入",
        [nameof(Export)] = "导出",
        [nameof(CopySummary)] = "复制摘要",
        [nameof(CopyConfig)] = "复制配置",
        [nameof(CopyIssues)] = "复制问题",
        [nameof(ProfileChecks)] = "配置检查",
        [nameof(Update)] = "更新",
        [nameof(UpdateAll)] = "全部更新",
        [nameof(Name)] = "名称",
        [nameof(Url)] = "URL",
        [nameof(UpdateIntervalMinutes)] = "更新间隔分钟数",
        [nameof(TrustPolicy)] = "信任策略",
        [nameof(UpdateNow)] = "立即更新",
        [nameof(SaveSubscription)] = "保存订阅",
        [nameof(CopySource)] = "复制来源",
        [nameof(CopyResult)] = "复制结果",
        [nameof(RefreshDiagnostics)] = "刷新诊断",
        [nameof(ExportDiagnostics)] = "导出诊断",
        [nameof(FilterLogsPlaceholder)] = "过滤日志，/正则/",
        [nameof(ClearFilters)] = "清除过滤",
        [nameof(CopyLogs)] = "复制日志",
        [nameof(RunAll)] = "全部运行",
        [nameof(RunChecks)] = "运行检查",
        [nameof(CopyReport)] = "复制报告",
        [nameof(ExportZip)] = "导出 Zip",
        [nameof(OpenLogs)] = "打开日志",
        [nameof(CopyPorts)] = "复制端口",
        [nameof(Target)] = "目标",
        [nameof(TestUrl)] = "测试 URL",
        [nameof(TestNetwork)] = "测试网络",
        [nameof(TestForward)] = "测试转发",
        [nameof(CopyForward)] = "复制转发",
        [nameof(ClearForward)] = "清除转发",
        [nameof(LaunchAtLogin)] = "开机启动",
        [nameof(StartMinimized)] = "启动时最小化",
        [nameof(AutoConnectSelectedProfile)] = "自动连接选中配置",
        [nameof(StartupDelaySeconds)] = "启动延迟秒数",
        [nameof(Theme)] = "主题",
        [nameof(Language)] = "语言",
        [nameof(PacBypassRules)] = "PAC 绕过规则",
        [nameof(LogRetentionDays)] = "日志保留天数",
        [nameof(UpdateChannel)] = "更新通道",
        [nameof(CoreExecutable)] = "内核程序",
        [nameof(UseDetected)] = "使用检测结果",
        [nameof(CheckVersion)] = "检查版本",
        [nameof(CopyPath)] = "复制路径",
        [nameof(LocalFolders)] = "本地文件夹",
        [nameof(CopyFolders)] = "复制文件夹",
        [nameof(AppData)] = "应用数据",
        [nameof(Runtime)] = "运行状态",
        [nameof(SaveSettings)] = "保存设置",
        [nameof(OpenDashboard)] = "打开面板",
        [nameof(CopyProxyAddress)] = "复制代理地址",
        [nameof(UpdateSubscription)] = "更新订阅",
        [nameof(OpenLogsFolder)] = "打开日志文件夹",
        [nameof(Quit)] = "退出",
        [nameof(ImportXTunnelProfile)] = "导入 x-tunnel 配置",
        [nameof(JsonFileType)] = "JSON",
        [nameof(ClipboardProfile)] = "剪贴板配置",
        [nameof(ExportProfile)] = "导出配置",
        [nameof(Connected)] = "已连接",
        [nameof(Degraded)] = "降级",
        [nameof(Starting)] = "启动中",
        [nameof(Stopping)] = "停止中",
        [nameof(Faulted)] = "故障",
        [nameof(Recovering)] = "恢复中",
        [nameof(Disconnected)] = "未连接",
        [nameof(SidecarNotRunning)] = "内核侧车未运行。",
        [nameof(CoreNotRunning)] = "内核未运行",
        [nameof(NotValidated)] = "未校验",
        [nameof(LastChecked)] = "上次检查",
        [nameof(Up)] = "上行",
        [nameof(Down)] = "下行",
        [nameof(RttWaiting)] = "等待 RTT",
        [nameof(WaitingForRtt)] = "等待 RTT",
        [nameof(AvgRtt)] = "平均 RTT",
        [nameof(Reconnects)] = "重连",
        [nameof(ProxyModeMetric)] = "代理模式",
        [nameof(NoListeners)] = "无监听",
        [nameof(Unknown)] = "未知",
        [nameof(Running)] = "运行中",
        [nameof(Channel)] = "通道",
        [nameof(SettingsSaved)] = "设置已保存",
        [nameof(LanguageRestartNotRequired)] = "语言会立即切换，并在下次启动时保持。",
    };

    private AppText(string languageCode, IReadOnlyDictionary<string, string> values)
    {
        LanguageCode = languageCode;
        _values = values;
    }

    private readonly IReadOnlyDictionary<string, string> _values;

    public static IReadOnlyList<LanguageOption> LanguageOptions { get; } =
    [
        new("en-US", "English"),
        new("zh-CN", "中文 (简体)")
    ];

    public string LanguageCode { get; }

    public static AppText For(string? language)
    {
        var normalized = NormalizeLanguage(language);
        return normalized == "zh-CN"
            ? new AppText(normalized, Chinese)
            : new AppText(normalized, English);
    }

    public static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "en-US";
        }

        var value = language.Trim();
        if (value.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-CN";
        }
        if (value.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return "en-US";
        }
        return "en-US";
    }

    public static CultureInfo CultureFor(string? language) => CultureInfo.GetCultureInfo(NormalizeLanguage(language));

    private string Get([CallerMemberName] string? key = null)
    {
        if (key is null)
        {
            return "";
        }
        if (_values.TryGetValue(key, out var value))
        {
            return value;
        }
        return English.TryGetValue(key, out var fallback) ? fallback : key;
    }

    public string AppTitle => Get();
    public string Connect => Get();
    public string Disconnect => Get();
    public string Restart => Get();
    public string Diagnostics => Get();
    public string Traffic => Get();
    public string Channels => Get();
    public string Core => Get();
    public string Issue => Get();
    public string Overview => Get();
    public string Profile => Get();
    public string Profiles => Get();
    public string Subscriptions => Get();
    public string Logs => Get();
    public string Settings => Get();
    public string Proxy => Get();
    public string Local => Get();
    public string Network => Get();
    public string Validation => Get();
    public string Control => Get();
    public string ProxyMode => Get();
    public string ProxyModeOff => Get();
    public string ProxyModeSystem => Get();
    public string ProxyModePac => Get();
    public string CorePathPlaceholder => Get();
    public string Save => Get();
    public string RestoreProxy => Get();
    public string ProxyAddress => Get();
    public string CopyStatus => Get();
    public string Open => Get();
    public string Listeners => Get();
    public string RecentLogs => Get();
    public string Copy => Get();
    public string RuntimeDetails => Get();
    public string CopyMetrics => Get();
    public string SearchProfiles => Get();
    public string SearchSubscriptions => Get();
    public string Clear => Get();
    public string Sort => Get();
    public string New => Get();
    public string Delete => Get();
    public string TestSelected => Get();
    public string TestVisible => Get();
    public string TestFastest => Get();
    public string Fastest => Get();
    public string ClearTests => Get();
    public string Startup => Get();
    public string ClearStartup => Get();
    public string ProfileNamePlaceholder => Get();
    public string SourcePlaceholder => Get();
    public string Fallback => Get();
    public string SecretRefPlaceholder => Get();
    public string ProfileSecretPlaceholder => Get();
    public string ApplyForm => Get();
    public string Validate => Get();
    public string Format => Get();
    public string ImportFile => Get();
    public string ImportClipboard => Get();
    public string Export => Get();
    public string CopySummary => Get();
    public string CopyConfig => Get();
    public string CopyIssues => Get();
    public string ProfileChecks => Get();
    public string Update => Get();
    public string UpdateAll => Get();
    public string Name => Get();
    public string Url => Get();
    public string UpdateIntervalMinutes => Get();
    public string TrustPolicy => Get();
    public string UpdateNow => Get();
    public string SaveSubscription => Get();
    public string CopySource => Get();
    public string CopyResult => Get();
    public string RefreshDiagnostics => Get();
    public string ExportDiagnostics => Get();
    public string FilterLogsPlaceholder => Get();
    public string ClearFilters => Get();
    public string CopyLogs => Get();
    public string RunAll => Get();
    public string RunChecks => Get();
    public string CopyReport => Get();
    public string ExportZip => Get();
    public string OpenLogs => Get();
    public string CopyPorts => Get();
    public string Target => Get();
    public string TestUrl => Get();
    public string TestNetwork => Get();
    public string TestForward => Get();
    public string CopyForward => Get();
    public string ClearForward => Get();
    public string LaunchAtLogin => Get();
    public string StartMinimized => Get();
    public string AutoConnectSelectedProfile => Get();
    public string StartupDelaySeconds => Get();
    public string Theme => Get();
    public string Language => Get();
    public string PacBypassRules => Get();
    public string LogRetentionDays => Get();
    public string UpdateChannel => Get();
    public string CoreExecutable => Get();
    public string UseDetected => Get();
    public string CheckVersion => Get();
    public string CopyPath => Get();
    public string LocalFolders => Get();
    public string CopyFolders => Get();
    public string AppData => Get();
    public string Runtime => Get();
    public string SaveSettings => Get();
    public string OpenDashboard => Get();
    public string CopyProxyAddress => Get();
    public string UpdateSubscription => Get();
    public string OpenLogsFolder => Get();
    public string Quit => Get();
    public string ImportXTunnelProfile => Get();
    public string JsonFileType => Get();
    public string ClipboardProfile => Get();
    public string ExportProfile => Get();
    public string Connected => Get();
    public string Degraded => Get();
    public string Starting => Get();
    public string Stopping => Get();
    public string Faulted => Get();
    public string Recovering => Get();
    public string Disconnected => Get();
    public string SidecarNotRunning => Get();
    public string CoreNotRunning => Get();
    public string NotValidated => Get();
    public string LastChecked => Get();
    public string Up => Get();
    public string Down => Get();
    public string RttWaiting => Get();
    public string WaitingForRtt => Get();
    public string AvgRtt => Get();
    public string Reconnects => Get();
    public string ProxyModeMetric => Get();
    public string NoListeners => Get();
    public string Unknown => Get();
    public string Running => Get();
    public string Channel => Get();
    public string SettingsSaved => Get();
    public string LanguageRestartNotRequired => Get();
}
