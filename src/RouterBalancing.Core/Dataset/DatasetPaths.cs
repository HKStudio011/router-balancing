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

    /// <summary>Prefix tên file hàng ngày cho record v1 có trường vượt ngưỡng inline.</summary>
    public const string LargeFileNamePrefix = "requests-large-";

    /// <summary>Prefix tên file manifest v2 có payload externalized.</summary>
    public const string PayloadFileNamePrefix = "requests-payload-";

    /// <summary>Thư mục gốc tương đối chứa source documents và binary payload.</summary>
    public const string PayloadRootDirectoryName = "payloads";

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

    /// <summary>Tên file large cho ngày UTC: <c>requests-large-YYYY-MM-DD.jsonl</c>.</summary>
    public static string LargeFileName(DateOnly utcDay) =>
        $"{LargeFileNamePrefix}{utcDay:yyyy-MM-dd}{FileNameExtension}";

    /// <summary>Tên file manifest payload cho ngày UTC: <c>requests-payload-YYYY-MM-DD.jsonl</c>.</summary>
    public static string PayloadFileName(DateOnly utcDay) =>
        $"{PayloadFileNamePrefix}{utcDay:yyyy-MM-dd}{FileNameExtension}";

    /// <summary>Thư mục artifact tương đối của một export; luôn dùng <c>/</c> trong manifest.</summary>
    public static string PayloadRelativeDirectory(DateOnly day, string exportId) =>
        $"{PayloadRootDirectoryName}/{day:yyyy-MM-dd}/{exportId}";

    /// <summary>Đường dẫn artifact tương đối của một export; luôn dùng <c>/</c> trong manifest.</summary>
    public static string PayloadRelativePath(DateOnly day, string exportId, string fileName) =>
        $"{PayloadRelativeDirectory(day, exportId)}/{fileName}";
}
