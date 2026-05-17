using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

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
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => _viewModel.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ShowWindow_OnClick(object? sender, EventArgs e)
    {
        if (_desktop?.MainWindow is null)
        {
            return;
        }
        _desktop.MainWindow.Show();
        _desktop.MainWindow.WindowState = WindowState.Normal;
        _desktop.MainWindow.Activate();
    }

    private void Connect_OnClick(object? sender, EventArgs e)
    {
        _viewModel?.ConnectCommand.Execute(null);
    }

    private void Disconnect_OnClick(object? sender, EventArgs e)
    {
        _viewModel?.DisconnectCommand.Execute(null);
    }

    private void Restart_OnClick(object? sender, EventArgs e)
    {
        _viewModel?.RestartCommand.Execute(null);
    }

    private void Diagnostics_OnClick(object? sender, EventArgs e)
    {
        ShowWindow_OnClick(sender, e);
        _viewModel?.RefreshDiagnosticsCommand.Execute(null);
    }

    private void RestoreProxy_OnClick(object? sender, EventArgs e)
    {
        _viewModel?.RestoreProxyCommand.Execute(null);
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
