using System.Text.Json.Serialization;

namespace RouterBalancing.Core.Update;

/// <summary>
/// Thông tin một GitHub Release — map trực tiếp từ JSON của GitHub API (releases/latest).
/// </summary>
/// <param name="TagName">Tag của release, ví dụ "v1.2.3".</param>
/// <param name="HtmlUrl">URL trang release trên GitHub.</param>
/// <param name="Body">Nội dung release notes; có thể null.</param>
/// <param name="Assets">Danh sách file đính kèm (zip cài đặt).</param>
public record ReleaseInfo(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("assets")] List<ReleaseAsset> Assets);

/// <summary>
/// Một file đính kèm của release — chỉ giữ đúng 2 trường cần để tải bản update.
/// </summary>
/// <param name="Name">Tên file, ví dụ "router-balancing-1.2.3-win-x64.zip".</param>
/// <param name="BrowserDownloadUrl">URL tải trực tiếp từ GitHub.</param>
public record ReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

/// <summary>
/// Dịch vụ kiểm tra, tải và cài bản update của ứng dụng.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>
    /// Phiên bản hiện tại của app (assembly version).
    /// </summary>
    string CurrentVersion { get; }

    /// <summary>
    /// Trả release mới nhất từ GitHub; <see langword="null"/> khi lỗi hoặc không phải Windows.
    /// </summary>
    Task<ReleaseInfo?> CheckForUpdatesAsync();

    /// <summary>
    /// Có bản mới hơn <see cref="CurrentVersion"/> không — so tag đã strip 'v' và pad số 0.
    /// </summary>
    bool IsUpdateAvailable(ReleaseInfo release);

    /// <summary>
    /// Tải zip của <paramref name="release"/> về thư mục tạm, báo tiến độ qua <paramref name="progress"/>;
    /// trả về đường dẫn file hoặc <see langword="null"/> khi lỗi.
    /// </summary>
    Task<string?> DownloadUpdateAsync(ReleaseInfo release, IProgress<int>? progress);

    /// <summary>
    /// Đường dẫn zip đã tải (nếu có).
    /// </summary>
    string? DownloadedZipPath { get; }

    /// <summary>
    /// Khởi chạy RouterBalancing.Updater.exe để cài bản update;
    /// <see langword="false"/> khi lỗi hoặc không phải Windows.
    /// </summary>
    Task<bool> InstallUpdateAsync();
}
