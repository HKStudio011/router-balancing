using System.Diagnostics;

namespace router_balancing_test.Update;

/// <summary>
/// Dummy process chạy PING.EXE từ thư mục temp — mô phỏng binary nằm trong appDir cho
/// test updater (kill process mồ côi / chẩn đoán lock file). Dispose lo trọn cleanup:
/// kill + chờ OS nhả handle exe + retry xoá thư mục.
/// </summary>
/// <remarks>
/// Windows giữ handle file exe tới khi process thoát hẳn và có thể ném
/// <see cref="UnauthorizedAccessException"/> (không chỉ <see cref="IOException"/>) khi xoá
/// thư mục — từng làm đỏ CI (run 40711439161, 40764737934) vì test chỉ catch IOException.
/// </remarks>
internal sealed class DummyAppDirProcess : IDisposable
{
    private readonly Process _process;

    /// <summary>Tạo thư mục temp, copy PING.EXE thành dummy.exe và start ở chế độ nền.</summary>
    public DummyAppDirProcess()
    {
        Dir = Path.Combine(Path.GetTempPath(), $"rb-tmp-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Dir);
        ExePath = Path.Combine(Dir, "dummy.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE"), ExePath);

        _process = Process.Start(new ProcessStartInfo(ExePath, "-t 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Không start được dummy process từ " + ExePath);
    }

    /// <summary>Thư mục temp chứa dummy.exe — mô phỏng appDir.</summary>
    public string Dir { get; }

    /// <summary>Đường dẫn dummy.exe.</summary>
    public string ExePath { get; }

    /// <summary>PID của dummy — dùng cho tham số skipPid.</summary>
    public int Id => _process.Id;

    /// <summary>Process còn sống hay không; không truy cập được (đã dispose) coi như đã thoát.</summary>
    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>Chờ process thoát tối đa <paramref name="millisecondsTimeout"/> ms.</summary>
    public bool WaitForExit(int millisecondsTimeout) => _process.WaitForExit(millisecondsTimeout);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                // Đợi OS thu hồi handle exe trước khi xoá — Kill trả về trước khi handle được nhả.
                _process.WaitForExit(5_000);
            }
        }
        catch
        {
            // Process đã thoát — không cần kill.
        }

        _process.Dispose();

        // Dọn best-effort: exe có thể còn bị giữ vài trăm ms sau khi process chết —
        // retry ngắn rồi bỏ qua (thư mục nằm trong %TEMP%).
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                System.IO.Directory.Delete(Dir, recursive: true);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
