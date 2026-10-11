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

    /// <inheritdoc />
    public string CurrentVersion { get; }

    /// <inheritdoc />
    public string? DownloadedZipPath { get; private set; }

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
    internal GitHubAppUpdateService(HttpClient http, string currentVersion)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(currentVersion);

        _http = http;
        CurrentVersion = currentVersion;
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

        try
        {
            var asset = release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                !a.Name.Contains("symbols", StringComparison.OrdinalIgnoreCase));

            if (asset is null) return null;

            // Ghi đè (FileMode.Create) thay vì reuse — bản tải dở lần trước có thể đã hỏng.
            var tempDir = Path.Combine(Path.GetTempPath(), UpdateFolderName);
            Directory.CreateDirectory(tempDir);
            var zipPath = Path.Combine(tempDir, asset.Name);

            var request = new HttpRequestMessage(HttpMethod.Get, asset.BrowserDownloadUrl);
            request.Headers.UserAgent.ParseAdd($"RouterBalancing/{CurrentVersion}");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return null;

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);

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

            DownloadedZipPath = zipPath;
            return zipPath;
        }
        catch (Exception)
        {
            // Lỗi mạng/đĩa → null để UI báo tải thất bại, không nuốt im lặng cũng không crash.
            return null;
        }
    }

    /// <inheritdoc />
    public Task<bool> InstallUpdateAsync()
    {
        // Updater là process Windows — ngoài Windows luôn trả false.
        if (!OperatingSystem.IsWindows()) return Task.FromResult(false);

        if (DownloadedZipPath is null || !File.Exists(DownloadedZipPath)) return Task.FromResult(false);

        try
        {
            var appDir = AppContext.BaseDirectory;
            var updaterPath = FindUpdaterPath(appDir);
            if (updaterPath is null) return Task.FromResult(false);

            // --pid để updater chờ app chết hẳn trước khi ghi đè file đang chạy.
            var psi = new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = $"--zip \"{DownloadedZipPath}\" --pid {Environment.ProcessId} --app-dir \"{appDir}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            };

            Process.Start(psi);
            return Task.FromResult(true);
        }
        catch (Exception)
        {
            // Không khởi động được updater (file hỏng, thiếu quyền...) → false để UI báo lỗi cài đặt.
            return Task.FromResult(false);
        }
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
