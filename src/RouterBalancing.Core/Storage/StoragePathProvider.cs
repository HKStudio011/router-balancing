namespace RouterBalancing.Core.Storage;

/// <summary>Định vị thư mục dữ liệu app — file DB nằm trong AppData theo spec.</summary>
public static class StoragePathProvider
{
    /// <summary>Thư mục dữ liệu: %AppData%/router-balancing.</summary>
    public static string GetDataDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "router-balancing");

    /// <summary>Đường dẫn file SQLite: %AppData%/router-balancing/router-balancing.db.</summary>
    public static string GetDatabasePath() =>
        Path.Combine(GetDataDirectory(), "router-balancing.db");

    /// <summary>
    /// Đường dẫn snapshot hàng đợi: %AppData%/router-balancing/queue-snapshot.json —
    /// persist request chưa dispatch trước khi app restart cài update (spec §8).
    /// </summary>
    public static string GetQueueSnapshotPath() =>
        Path.Combine(GetDataDirectory(), "queue-snapshot.json");
}
