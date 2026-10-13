using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace RouterBalancing.Core.Update;

/// <summary>
/// Triển khai <see cref="IAppUpdateService"/> trên GitHub Releases của HKStudio011/router-balancing.
/// Chỉ hoạt động trên Windows (updater là process Windows); các method trả sentinel (null/false) thay vì ném lỗi.
/// </summary>
public sealed class GitHubAppUpdateService : IAppUpdateService
{
    private const string RepoOwner = "HKStudio011";
    private const string RepoName = "router-balancing";
    private const string UpdaterFileName = "RouterBalancing.Updater.exe";
    private const string UpdateFolderName = "RouterBalancing_Update";

    private readonly HttpClient _http;

    /// <summary>
    /// Public key (SPKI Base64) verify chữ ký zip update — production dùng
    /// <see cref="PackageSignatureVerifier.ReleasePublicKeyBase64"/>; test inject keypair khác.
    /// </summary>
    private readonly string _signingPublicKeyBase64;

    /// <inheritdoc />
    public string CurrentVersion { get; }

    /// <inheritdoc />
    public string? DownloadedZipPath { get; private set; }

    /// <summary>
    /// Đường dẫn file chữ ký (.sig) đã tải kèm zip (nếu có).
    /// </summary>
    internal string? DownloadedSignaturePath { get; private set; }

    /// <summary>
    /// Tạo service với phiên bản lấy từ entry assembly của app.
    /// </summary>
    /// <param name="http">HttpClient dùng cho mọi request tới GitHub (GitHub yêu cầu header User-Agent).</param>
    public GitHubAppUpdateService(HttpClient http)
        : this(http, Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0")
    {
    }

    // Overload nội bộ cho test: Assembly.GetEntryAssembly() trong test host trả về testhost
    // chứ không phải app — không ép được thì mọi assertion về version sẽ phụ thuộc runner.
    internal GitHubAppUpdateService(HttpClient http, string currentVersion, string? signingPublicKeyBase64 = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(currentVersion);

        _http = http;
        CurrentVersion = currentVersion;
        _signingPublicKeyBase64 = signingPublicKeyBase64 ?? PackageSignatureVerifier.ReleasePublicKeyBase64;
    }

    /// <inheritdoc />
    public async Task<ReleaseInfo?> CheckForUpdatesAsync()
    {
        // Update chỉ dành cho app Windows thật — trả null để caller báo "không kiểm tra được".
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");
            request.Headers.UserAgent.ParseAdd($"RouterBalancing/{CurrentVersion}");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ReleaseInfo>(json);
        }
        catch (Exception)
        {
            // Mất mạng/JSON hỏng → null để UpdateCoordinator bỏ qua lần kiểm tra này thay vì crash app.
            return null;
        }
    }

    /// <inheritdoc />
    public bool IsUpdateAvailable(ReleaseInfo release)
    {
        // So sánh thuần (không I/O) nên không cần Windows guard — UI hiển thị trạng thái update ở mọi nền tảng.
        try
        {
            var tag = release.TagName.TrimStart('v');
            return CompareVersions(tag, CurrentVersion) > 0;
        }
        catch (Exception)
        {
            // Release rỗng/khỏng hợp lệ → coi như không có bản mới, không để exception lọt ra caller.
            return false;
        }
    }

    /// <summary>
    /// So sánh hai version từng bậc, pad số 0 khi thiếu thành phần (v1.1 ≡ 1.1.0.0).
    /// Thành phần không parse được thành số coi như 0.
    /// </summary>
    private static int CompareVersions(string candidate, string current)
    {
        var candidateParts = candidate.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var currentParts = current.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var maxParts = Math.Max(candidateParts.Length, currentParts.Length);

        for (var i = 0; i < maxParts; i++)
        {
            var left = i < candidateParts.Length && int.TryParse(candidateParts[i], out var p1) ? p1 : 0;
            var right = i < currentParts.Length && int.TryParse(currentParts[i], out var p2) ? p2 : 0;
            if (left != right) return left.CompareTo(right);
        }

        return 0;
    }

    /// <inheritdoc />
    public async Task<string?> DownloadUpdateAsync(ReleaseInfo release, IProgress<int>? progress)
    {
        // Tải file + ghi đĩa là thao tác Windows-only theo contract của updater.
        if (!OperatingSystem.IsWindows()) return null;

        // Invalidate NGAY khi bắt đầu attempt mới: FileStream bên dưới dùng FileMode.Create nên sẽ
        // truncate zip cũ — nếu attempt này fail giữa chừng, path không được trỏ tới file rác
        // (InstallUpdateAsync chỉ check File.Exists nên sẽ cài bản hỏng).
        DownloadedZipPath = null;
        DownloadedSignaturePath = null;

        string? zipPath = null;
        string? signaturePath = null;
        try
        {
            var asset = release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                !a.Name.Contains("symbols", StringComparison.OrdinalIgnoreCase));

            if (asset is null) return null;

            // Fail-closed: release không có file .sig → không tin được zip, từ chối update.
            // (Các release trước khi triển khai ký sẽ không cập nhật được — intended.)
            var signatureAsset = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, asset.Name + ".sig", StringComparison.OrdinalIgnoreCase));
            if (signatureAsset is null)
            {
                DeletePartialFile(zipPath);
                return null;
            }

            // Ghi đè (FileMode.Create) thay vì reuse — bản tải dở lần trước có thể đã hỏng.
            var tempDir = Path.Combine(Path.GetTempPath(), UpdateFolderName);
            Directory.CreateDirectory(tempDir);
            zipPath = Path.Combine(tempDir, asset.Name);
            signaturePath = zipPath + ".sig";

            var request = new HttpRequestMessage(HttpMethod.Get, asset.BrowserDownloadUrl);
            request.Headers.UserAgent.ParseAdd($"RouterBalancing/{CurrentVersion}");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return null;

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            await using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long readBytes = 0;
                var lastReported = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    readBytes += bytesRead;

                    // Chỉ báo khi % tăng — tránh spam progress với 81920 byte mỗi lần đọc.
                    if (totalBytes > 0 && progress is not null)
                    {
                        var pct = (int)(readBytes * 100 / totalBytes);
                        if (pct > lastReported)
                        {
                            lastReported = pct;
                            progress.Report(pct);
                        }
                    }
                }
            }

            // Tải chữ ký (file nhỏ, đọc trọn 1 lần — không cần progress)
            var signatureRequest = new HttpRequestMessage(HttpMethod.Get, signatureAsset.BrowserDownloadUrl);
            signatureRequest.Headers.UserAgent.ParseAdd($"RouterBalancing/{CurrentVersion}");

            using var signatureResponse = await _http.SendAsync(signatureRequest);
            if (!signatureResponse.IsSuccessStatusCode)
            {
                DeletePartialFile(zipPath);
                return null;
            }

            var signatureBytes = await signatureResponse.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(signaturePath, signatureBytes);

            DownloadedSignaturePath = signaturePath;
            DownloadedZipPath = zipPath;
            return zipPath;
        }
        catch (Exception)
        {
            // Lỗi mạng/đĩa → xóa zip + sig dở dang rồi trả null để UI báo tải thất bại.
            DeletePartialFile(zipPath);
            DeletePartialFile(signaturePath);
            return null;
        }
    }

    // File.Delete không ném khi file không tồn tại; có thể fail nếu file bị khóa — khi đó giữ nguyên,
    // vô hại vì DownloadedZipPath đã null nên updater không thể dùng nhầm file rác.
    private static void DeletePartialFile(string? zipPath)
    {
        if (zipPath is null) return;

        try
        {
            File.Delete(zipPath);
        }
        catch (Exception)
        {
            // Không xóa được file rác trong temp (đĩa đầy/file khóa) — đã xử lý bằng cách không trỏ path tới nó.
        }
    }

    /// <inheritdoc />
    public Task<bool> InstallUpdateAsync()
    {
        // Updater là process Windows — ngoài Windows luôn trả false.
        if (!OperatingSystem.IsWindows()) return Task.FromResult(false);

        if (DownloadedZipPath is null || !File.Exists(DownloadedZipPath)) return Task.FromResult(false);

        // Verify chữ ký TRƯỚC khi khởi động updater (threat model: zip bị tamper trên đĩa
        // sau khi tải hoặc release bị giả mạo) — fail-closed: không xác thực được thì xóa zip.
        if (DownloadedSignaturePath is null ||
            !PackageSignatureVerifier.Verify(DownloadedZipPath, DownloadedSignaturePath, _signingPublicKeyBase64))
        {
            DeletePartialFile(DownloadedZipPath);
            DeletePartialFile(DownloadedSignaturePath);
            DownloadedZipPath = null;
            DownloadedSignaturePath = null;
            return Task.FromResult(false);
        }

        try
        {
            var appDir = AppContext.BaseDirectory;
            if (FindUpdaterPath(appDir) is null) return Task.FromResult(false);

            // Updater phải chạy từ shadow copy ngoài appDir: chạy từ chính appDir thì
            // process giữ khóa RouterBalancing.Updater.dll → zip extract (overwrite)
            // không ghi đè được chính dll đó → "file is being used by another process".
            var shadowDir = PrepareUpdaterShadowCopy(appDir);
            if (shadowDir is null) return Task.FromResult(false);

            var psi = BuildUpdaterStartInfo(DownloadedZipPath, Environment.ProcessId, appDir);
            psi.FileName = Path.Combine(shadowDir, UpdaterFileName);

            Process.Start(psi);
            return Task.FromResult(true);
        }
        catch (Exception)
        {
            // Không khởi động được updater (file hỏng, thiếu quyền...) → false để UI báo lỗi cài đặt.
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Copy file runtime của updater (exe/dll/deps/runtimeconfig, bỏ pdb) sang
    /// <c>%TEMP%\RouterBalancing_Updater</c> để process mới không giữ khóa file trong appDir.
    /// </summary>
    /// <returns>Thư mục chứa bản copy; null khi copy thất bại (đĩa đầy/thiếu quyền).</returns>
    internal static string? PrepareUpdaterShadowCopy(string appDir)
    {
        try
        {
            var shadowDir = Path.Combine(
                Path.GetTempPath(), "RouterBalancing_Updater", $"v{Environment.ProcessId}-{Environment.TickCount64}");
            Directory.CreateDirectory(shadowDir);

            foreach (var file in Directory.EnumerateFiles(appDir, "RouterBalancing.Updater.*"))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Copy(file, Path.Combine(shadowDir, name), overwrite: true);
            }

            // Không có exe trong bản copy → updater không chạy được, coi như prepare fail
            return File.Exists(Path.Combine(shadowDir, UpdaterFileName)) ? shadowDir : null;
        }
        catch (Exception)
        {
            // Copy fail → caller trả false; thư mục dở dang best-effort để System temp dọn.
            return null;
        }
    }

    /// <summary>
    /// Xây <see cref="ProcessStartInfo"/> khởi chạy updater với đúng sequence
    /// <c>--zip &lt;zip&gt; --pid &lt;pid&gt; --app-dir &lt;dir&gt;</c> mà parser của updater yêu cầu.
    /// </summary>
    /// <param name="zipPath">Đường dẫn zip update đã tải.</param>
    /// <param name="parentPid">PID của app để updater chờ process thoát trước khi ghi đè.</param>
    /// <param name="appDir">Thư mục cài đặt của app (thường là <c>AppContext.BaseDirectory</c>).</param>
    internal static ProcessStartInfo BuildUpdaterStartInfo(string zipPath, int parentPid, string appDir)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        };

        // Dùng ArgumentList thay vì nối chuỗi `Arguments` thủ công: appDir luôn kết thúc bằng '\',
        // nên `--app-dir "C:\...\net10.0\"` bị Windows parse theo odd-backslash rule — '\' cuối
        // escape dấu quote đóng, updater nhận arg thừa ký tự '"' → backup bị skip và extract
        // tạo thư mục lỗi. ArgumentList để .NET tự quote từng phần tử đúng chuẩn CommandLineToArgvW.
        psi.ArgumentList.Add("--zip");
        psi.ArgumentList.Add(zipPath);

        // --pid để updater chờ app chết hẳn trước khi ghi đè file đang chạy.
        psi.ArgumentList.Add("--pid");
        psi.ArgumentList.Add(parentPid.ToString());

        psi.ArgumentList.Add("--app-dir");
        psi.ArgumentList.Add(appDir);

        return psi;
    }

    private static string? FindUpdaterPath(string appDir)
    {
        var candidates = new[]
        {
            Path.Combine(appDir, UpdaterFileName),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
