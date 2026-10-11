using System.Diagnostics;
using System.IO.Compression;

if (args is not ["--zip", _, "--pid", _, "--app-dir", _])
{
    Console.Error.WriteLine("Usage: RouterBalancing.Updater --zip <zip-path> --pid <parent-pid> --app-dir <app-directory>");
    goto error;
}

var zipPath = args[1];
var pid = int.Parse(args[3]);
var appDir = args[5];

if (!File.Exists(zipPath))
{
    Console.Error.WriteLine($"Zip not found: {zipPath}");
    goto error;
}

try
{
    var parent = Process.GetProcessById(pid);
    // 60s thay vì 30s: app MAUI Blazor Hybrid nặng hơn DMFT (WebView, JIT, flush tài nguyên)
    // nên cần thêm thời gian thoát — xem spec §10.
    if (!parent.WaitForExit(60_000))
    {
        Console.Error.WriteLine("Timed out waiting for parent process to exit");
        goto error;
    }
}
// pid đã không còn (app thoát trước đó) — điều kiện chờ đạt được, tiếp tục.
catch (ArgumentException) { }

try
{
    // router-balancing.exe là target exe của app chính; backup best-effort —
    // file bị lock hoặc thiếu quyền không được chặn luồng cập nhật.
    var exePath = Path.Combine(appDir, "router-balancing.exe");
    var backupPath = exePath + ".bak";
    try { File.Copy(exePath, backupPath, overwrite: true); } catch { }

    // Tự move bản thân sang .old vì file đang chạy không thể bị ghi đè bởi zip;
    // best-effort — bỏ qua lỗi để tiếp tục extract.
    var selfPath = Path.Combine(appDir, "RouterBalancing.Updater.exe");
    var selfBackup = selfPath + ".old";
    try { File.Move(selfPath, selfBackup, overwrite: true); } catch { }

    ZipFile.ExtractToDirectory(zipPath, appDir, overwriteFiles: true);

    Process.Start(new ProcessStartInfo
    {
        FileName = exePath,
        UseShellExecute = true,
        WorkingDirectory = appDir
    });

    // Dọn dẹp best-effort — file rác còn sót không ảnh hưởng functional của update.
    try { File.Delete(backupPath); } catch { }
    try { File.Delete(selfBackup); } catch { }
    try { File.Delete(zipPath); } catch { }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Update failed: {ex.Message}");
    goto error;
}

error:
    Console.Error.WriteLine("Update failed.");
    Console.WriteLine("Press Enter to exit...");
    Console.ReadLine();
    return 1;
