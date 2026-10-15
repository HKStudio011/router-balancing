using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Khởi động và theo dõi process Go sidecar (opencode-proxy). Sidecar in
/// "LISTENING <port>" ra stdout sau khi bind — service parse dòng đầu tiên để
/// biết port; process chết được restart ở lần EnsureStartedAsync kế tiếp.
/// </summary>
public sealed partial class OpenCodeSidecarService : IOpenCodeSidecarService
{
    private readonly string _binaryPath;
    private readonly ILogger<OpenCodeSidecarService> _logger;
    private readonly object _gate = new();

    private Process? _process;
    private string? _baseUrl;
    private bool _disposed;

    /// <summary>
    /// Tạo service quản lý sidecar tại <paramref name="binaryPath"/>.
    /// </summary>
    /// <param name="binaryPath">Đường dẫn tuyệt đối tới opencode-proxy.exe.</param>
    /// <param name="logger">Logger ghi sự kiện start/restart/crash của process.</param>
    public OpenCodeSidecarService(string binaryPath, ILogger<OpenCodeSidecarService> logger)
    {
        _binaryPath = binaryPath;
        _logger = logger;
    }

    /// <inheritdoc />
    public string? BaseUrl
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false } ? _baseUrl : null;
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> EnsureStartedAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }
            if (_process is { HasExited: false })
            {
                return true;
            }
        }

        if (!File.Exists(_binaryPath))
        {
            _logger.LogWarning("OpenCode sidecar binary missing at {Path}", _binaryPath);
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _binaryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start OpenCode sidecar from {Path}", _binaryPath);
            return false;
        }
        if (process is null)
        {
            _logger.LogError("Process.Start returned null for {Path}", _binaryPath);
            return false;
        }

        string? baseUrl;
        try
        {
            // Sidecar in đúng 1 dòng "LISTENING <port>" rồi Serve() blocking —
            // ReadLine trả null nghĩa là process chết trước khi bind xong.
            var line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            var match = ListeningRegex().Match(line ?? string.Empty);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var port))
            {
                _logger.LogError("Sidecar did not report LISTENING port (got: {Line})", line);
                TryKill(process);
                return false;
            }
            baseUrl = $"http://127.0.0.1:{port}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed reading sidecar stdout");
            TryKill(process);
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                TryKill(process);
                return false;
            }
            // Đóng race: EnsureStartedAsync song song — process vừa start giữ nguyên,
            // process cũ (nếu có) đã exited nên không cần kill.
            _process = process;
            _baseUrl = baseUrl;
        }

        _ = Task.Run(() => DrainAndWatchAsync(process), CancellationToken.None);
        _logger.LogInformation("OpenCode sidecar started on {BaseUrl}", baseUrl);
        return true;
    }

    /// <summary>
    /// Kill sidecar process nếu đang chạy — dùng khi test restart logic.
    /// </summary>
    internal void KillForTest()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
            _baseUrl = null;
        }
        if (process is not null)
        {
            TryKill(process);
            process.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        KillForTest();
    }

    // stderr liên tục đọc để không full buffer (sidecar ít log nhưng tránh deadlock).
    private async Task DrainAndWatchAsync(Process process)
    {
        try
        {
            _ = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // Process đã bị dispose/kill — không cần xử lý thêm.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                    _baseUrl = null;
                }
            }
            process.Dispose();
            _logger.LogWarning("OpenCode sidecar process exited");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Đã thoát giữa chừng hoặc không có quyền — process sẽ tự giải phóng.
        }
    }

    [GeneratedRegex(@"^LISTENING (\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ListeningRegex();
}
