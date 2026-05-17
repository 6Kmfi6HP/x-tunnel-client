namespace XTunnelClient.Core;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "x-tunnel-client");
        Profiles = Path.Combine(Root, "profiles");
        Subscriptions = Path.Combine(Root, "subscriptions");
        Runtime = Path.Combine(Root, "runtime");
        Logs = Path.Combine(Root, "logs");
        Core = Path.Combine(Root, "core");
        Updates = Path.Combine(Root, "updates");
        Database = Path.Combine(Root, "app.db");
        Settings = Path.Combine(Root, "settings.json");
        ReadyFile = Path.Combine(Runtime, "ready.json");
        TokenFile = Path.Combine(Runtime, "token");
        RuntimeConfig = Path.Combine(Runtime, "active.json");
        SupervisorLock = Path.Combine(Runtime, "supervisor.lock");
        GuiLog = Path.Combine(Logs, "gui.log");
    }

    public string Root { get; }
    public string Profiles { get; }
    public string Subscriptions { get; }
    public string Runtime { get; }
    public string Logs { get; }
    public string Core { get; }
    public string Updates { get; }
    public string Database { get; }
    public string Settings { get; }
    public string ReadyFile { get; }
    public string TokenFile { get; }
    public string RuntimeConfig { get; }
    public string SupervisorLock { get; }
    public string GuiLog { get; }

    public void Ensure()
    {
        foreach (var path in new[] { Root, Profiles, Subscriptions, Runtime, Logs, Core, Updates })
        {
            Directory.CreateDirectory(path);
        }
    }

    public string CoreLogPath() => Path.Combine(Logs, $"core-{DateTime.UtcNow:yyyyMMdd}.log");
}

public sealed class CoreLocator
{
    private readonly AppPaths _paths;

    public CoreLocator(AppPaths paths)
    {
        _paths = paths;
    }

    public string? Resolve(AppSettings settings)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.CorePath))
        {
            candidates.Add(settings.CorePath);
        }

        var envPath = Environment.GetEnvironmentVariable("XTUNNEL_CORE_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            candidates.Add(envPath);
        }

        candidates.Add(Path.Combine(_paths.Core, "x-tunnel.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "core", "x-tunnel.exe"));
        candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "x-tunnel", "build", "x-tunnel.exe")));
        candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "x-tunnel", "cmd", "x-tunnel", "x-tunnel.exe")));

        return candidates.FirstOrDefault(File.Exists);
    }
}
