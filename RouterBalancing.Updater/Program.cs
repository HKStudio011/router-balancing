using System.Diagnostics;
using RouterBalancing.Updater;

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

// Staging được tạo trong try và luôn dọn trong finally — kể cả khi abort trước
// khi đụng appDir, không để lại thư mục trích dở dưới %TEMP%.
string? stagingDir = null;
try
{
    // 1. Trích zip sang staging — appDir chưa bị ghi gì, trích fail không để lại hỗn độn.
    stagingDir = UpdateStager.ExtractToStaging(zipPath);

    // 1b. Zip không phải bản self-contained → abort trước khi đụng appDir. Ghi đè zip
    //     FDD sẽ chuyển cài đặt sang framework-dependent giữa chừng: hostfxr self-contained
    //     cạnh exe lẫn lộn với runtime hệ thống → lỗi ".NET missing"/hostfxr khi khởi động.
    if (!UpdateStager.VerifySelfContained(stagingDir))
    {
        Console.Error.WriteLine(
            "Zip is not a self-contained build — update aborted, application folder untouched.");
        goto error;
    }

    // 2. Chờ mọi file đích hết lock. Vẫn lock sau timeout → abort với appDir nguyên vẹn
    //    (trước đây extract thẳng vào appDir dở dang giữa chừng làm lẫn runtime hai kiểu).
    if (!UpdateStager.WaitUntilWritable(stagingDir, appDir, TimeSpan.FromSeconds(60)))
    {
        Console.Error.WriteLine(
            "Timed out waiting for application files to be released — update aborted, application folder untouched.");
        goto error;
    }

    // router-balancing.exe là target exe của app chính; backup best-effort —
    // file bị thiếu quyền không được chặn luồng cập nhật.
    var exePath = Path.Combine(appDir, "router-balancing.exe");
    var backupPath = exePath + ".bak";
    try { File.Copy(exePath, backupPath, overwrite: true); } catch { }

    // 3. Ghi staging → appDir (đã verify ghi được ở bước 2).
    UpdateStager.CopyDirectory(stagingDir, appDir);

    Process.Start(new ProcessStartInfo
    {
        FileName = exePath,
        UseShellExecute = true,
        WorkingDirectory = appDir
    });

    // Dọn dẹp best-effort — file rác còn sót không ảnh hưởng functional của update.
    try { File.Delete(backupPath); } catch { }
    try { File.Delete(zipPath); } catch { }
    try { Directory.Delete(stagingDir, recursive: true); } catch { }

    CleanupSelfTempDir();

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Update failed: {ex.Message}");
    goto error;
}
finally
{
    if (stagingDir is not null && Directory.Exists(stagingDir))
    {
        try { Directory.Delete(stagingDir, recursive: true); } catch { }
    }
}

/// <summary>
/// Dọn shadow copy temp của chính mình (InstallUpdateAsync đã copy trước): dll/deps
/// xóa được, exe đang chạy chỉ move được sang .old — System temp dọn nốt phần còn lại.
/// </summary>
static void CleanupSelfTempDir()
{
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
}

error:
    Console.Error.WriteLine("Update failed.");
    Console.WriteLine("Press Enter to exit...");
    Console.ReadLine();
    return 1;
