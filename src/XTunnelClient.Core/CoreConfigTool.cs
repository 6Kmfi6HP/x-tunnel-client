using System.Diagnostics;

namespace XTunnelClient.Core;

public sealed class CoreConfigTool
{
    public async Task CheckFileAsync(string corePath, string configPath, CancellationToken cancellationToken = default)
    {
        _ = await RunAsync(corePath, "-check-config", configPath, null, cancellationToken);
    }

    public async Task CheckJsonAsync(string corePath, string json, CancellationToken cancellationToken = default)
    {
        _ = await RunAsync(corePath, "-check-config", "-", json, cancellationToken);
    }

    public async Task<string> FormatFileAsync(string corePath, string configPath, CancellationToken cancellationToken = default)
    {
        return await RunAsync(corePath, "-format-config", configPath, null, cancellationToken);
    }

    public async Task<string> FormatJsonAsync(string corePath, string json, CancellationToken cancellationToken = default)
    {
        return await RunAsync(corePath, "-format-config", "-", json, cancellationToken);
    }

    private static async Task<string> RunAsync(string corePath, string flag, string pathOrDash, string? stdin, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(corePath) || !File.Exists(corePath))
        {
            throw new FileNotFoundException("找不到 x-tunnel.exe", corePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = corePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null
        };
        startInfo.ArgumentList.Add(flag);
        startInfo.ArgumentList.Add(pathOrDash);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("启动 core config 工具失败");
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException(RuntimeConfigService.Redact(message.Trim()));
        }
        return stdout;
    }
}
