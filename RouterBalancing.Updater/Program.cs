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

    // Updater chạy từ shadow copy trong %TEMP% (InstallUpdateAsync đã copy trước) —
    // không file nào trong appDir bị chính process này giữ khóa nên extract đè an toàn,
    // kể cả RouterBalancing.Updater.dll/exe cũ.
    ZipFile.ExtractToDirectory(zipPath, appDir, overwriteFiles: true);

    Process.Start(new ProcessStartInfo
    {
        FileName = exePath,
        UseShellExecute = true,
        WorkingDirectory = appDir
    });

    // Dọn dẹp best-effort — file rác còn sót không ảnh hưởng functional của update.
    try { File.Delete(backupPath); } catch { }
    try { File.Delete(zipPath); } catch { }

    // Dọn shadow copy temp của chính mình: dll/deps xóa được, exe đang chạy chỉ move được
    // sang .old — System temp cleanup dọn nốt phần còn lại.
    var selfDir = AppContext.BaseDirectory;
    try
    {
        foreach (var file in Directory.EnumerateFiles(selfDir))
        {
            try
            {
                if (string.Equals(Path.GetFileName(file), Path.GetFileName(Environment.ProcessPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(file, file + ".old", overwrite: true);
                }
                else
                {
                    File.Delete(file);
                }
            }
            catch { }
        }
    }
    catch { }

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
