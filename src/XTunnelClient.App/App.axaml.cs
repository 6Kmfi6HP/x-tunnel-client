using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using XTunnelClient.Core;

namespace XTunnelClient.App;

public partial class App : Application
{
    private MainViewModel? _viewModel;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _viewModel = new MainViewModel();
            DataContext = _viewModel;
            var window = new MainWindow(_viewModel);
            if (ShouldStartMinimized(desktop.Args, _viewModel.Settings))
            {
                window.ShowInTaskbar = false;
                window.Opened += (_, _) => window.Hide();
            }
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => _viewModel.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static bool ShouldStartMinimized(string[]? args, AppSettings settings)
    {
        return settings.StartMinimized
            || args?.Any(x => x.Equals("--minimized", StringComparison.OrdinalIgnoreCase)
                || x.Equals("/minimized", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private void ShowWindow_OnClick(object? sender, EventArgs e)
    {
        if (_desktop?.MainWindow is null)
        {
            return;
        }
        _desktop.MainWindow.Show();
        _desktop.MainWindow.ShowInTaskbar = true;
        _desktop.MainWindow.WindowState = WindowState.Normal;
        _desktop.MainWindow.Activate();
    }

    private void Diagnostics_OnClick(object? sender, EventArgs e)
    {
        ShowWindow_OnClick(sender, e);
        _viewModel?.RefreshDiagnosticsCommand.Execute(null);
    }

    private async void Quit_OnClick(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.ShutdownAsync();
        }
        _desktop?.Shutdown();
    }
}
