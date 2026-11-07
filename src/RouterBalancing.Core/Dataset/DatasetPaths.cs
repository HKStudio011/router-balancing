using RouterBalancing.Core.Storage;
using RouterBalancing.Core.Tools;

namespace RouterBalancing.Core.Dataset;

/// <summary>
/// Định vị file dataset JSONL: thư mục đích (mặc định %AppData%/router-balancing/dataset)
/// và tên file theo ngày UTC <c>requests-YYYY-MM-DD.jsonl</c> (spec §7.6).
/// </summary>
public static class DatasetPaths
{
    /// <summary>Prefix tên file hàng ngày.</summary>
    public const string FileNamePrefix = "requests-";

    /// <summary>Mở rộng tên file JSONL.</summary>
    public const string FileNameExtension = ".jsonl";

    /// <summary>Thư mục mặc định: %AppData%/router-balancing/dataset — nằm cạnh DB để purge/backup theo cùng một chỗ.</summary>
    public static string DefaultDirectory() =>
        Path.Combine(StoragePathProvider.GetDataDirectory(), "dataset");

    /// <summary>
    /// Giải đường dẫn người dùng set trong Settings: rỗng → <see cref="DefaultDirectory"/>;
    /// expand <c>~</c> bằng chung helper với config opencode (không copy logic);
    /// đường dẫn khác giữ nguyên.
    /// </summary>
    public static string Resolve(string? setting) =>
        string.IsNullOrWhiteSpace(setting)
            ? DefaultDirectory()
            : OpenCodeConfigPaths.ExpandHome(setting);

    /// <summary>Tên file cho ngày UTC: <c>requests-YYYY-MM-DD.jsonl</c>.</summary>
    public static string FileName(DateOnly utcDay) =>
        $"{FileNamePrefix}{utcDay:yyyy-MM-dd}{FileNameExtension}";
}
