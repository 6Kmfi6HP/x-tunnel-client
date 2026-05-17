using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace XTunnelClient.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
        : this(new MainViewModel())
    {
    }

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
        Closing += async (_, _) => await _viewModel.ShutdownAsync();
    }

    private async void ImportFile_OnClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Import x-tunnel profile",
            FileTypeFilter =
            [
                new FilePickerFileType("JSON") { Patterns = ["*.json"] },
                FilePickerFileTypes.All
            ]
        });
        var file = files.FirstOrDefault();
        if (file is null)
        {
            return;
        }
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        await _viewModel.ImportProfileJsonAsync(await reader.ReadToEndAsync(), Path.GetFileNameWithoutExtension(file.Name));
    }

    private async void ImportClipboard_OnClick(object? sender, RoutedEventArgs e)
    {
        var text = await Clipboard!.TryGetTextAsync();
        if (!string.IsNullOrWhiteSpace(text))
        {
            await _viewModel.ImportProfileJsonAsync(text, "Clipboard profile");
        }
    }

    private async void ExportProfile_OnClick(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export profile",
            SuggestedFileName = "x-tunnel-profile.json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
        });
        if (file is null)
        {
            return;
        }
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(_viewModel.ExportSelectedProfile());
    }

    private async void ExportDiagnostics_OnClick(object? sender, RoutedEventArgs e)
    {
        var path = await _viewModel.ExportDiagnosticsAsync();
        _viewModel.ErrorText = $"Diagnostics exported: {path}";
    }
}
