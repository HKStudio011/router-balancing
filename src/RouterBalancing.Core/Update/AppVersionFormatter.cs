namespace RouterBalancing.Core.Update;

/// <summary>
/// Định dạng <see cref="Version"/> thành chuỗi ngắn để hiển thị trên UI (footer/Settings):
/// luôn tối thiểu Major.Minor, cắt các số 0 ở cuối.
/// </summary>
public static class AppVersionFormatter
{
    /// <summary>
    /// Parse chuỗi version rồi định dạng dạng ngắn — convenience cho
    /// <c>IAppUpdateService.CurrentVersion</c> (UI không cần tự <c>Version.Parse</c>).
    /// </summary>
    /// <param name="version">Chuỗi version hợp lệ theo <see cref="Version.Parse(string)"/> (vd. "1.0.3.0").</param>
    /// <returns>Chuỗi version dạng ngắn, tối thiểu 2 phần (Major.Minor).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="version"/> là null.</exception>
    /// <exception cref="ArgumentException"><paramref name="version"/> không parse được thành <see cref="Version"/>.</exception>
    public static string Format(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return Format(Version.Parse(version));
    }

    /// <summary>
    /// Chuyển version sang dạng ngắn: 1.0.0.0 → "1.0", 1.0.3.0 → "1.0.3", 1.0.0.4 → "1.0.0.4".
    /// </summary>
    /// <param name="version">Version cần định dạng; không nhận null.</param>
    /// <returns>Chuỗi version dạng ngắn, tối thiểu 2 phần (Major.Minor).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="version"/> là null.</exception>
    public static string Format(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);

        // Version 2/3 thành phần cho Build/Revision = -1 (undefined) — chỉ lấy component có thật.
        var parts = new List<int> { version.Major, version.Minor };
        if (version.Build >= 0)
        {
            parts.Add(version.Build);
        }

        if (version.Revision >= 0)
        {
            parts.Add(version.Revision);
        }

        // Cắt số 0 cuối nhưng giữ tối thiểu Major.Minor — 1.0.0.0 → "1.0", 1.0.3.0 → "1.0.3".
        var end = parts.Count;
        while (end > 2 && parts[end - 1] == 0)
        {
            end--;
        }

        return string.Join('.', parts.Take(end));
    }
}
