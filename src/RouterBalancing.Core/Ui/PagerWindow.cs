namespace RouterBalancing.Core.Ui;

/// <summary>
/// Tính dãy nút số trang cho thanh phân trang.
/// Số nguyên = trang bấm được; <see langword="null"/> = dấu "…" (đại diện khoảng bị bỏ qua).
/// </summary>
public static class PagerWindow
{
    /// <summary>Số trang tối đa mà hiện đủ 1..N; lớn hơn thì thu gọn còn cửa sổ + ellipsis.</summary>
    public const int ShowAllThreshold = 7;

    /// <summary>Bán kính quanh trang hiện tại khi thu gọn (±2 → tối đa 5 số bấm ở giữa).</summary>
    private const int Radius = 2;

    /// <summary>
    /// Trả về dãy nút trang cho <paramref name="page"/> (clamp về [1, pageCount]).
    /// pageCount ≤ <see cref="ShowAllThreshold"/>: hiện đủ 1..pageCount.
    /// Lớn hơn: 1 … (page±<see cref="Radius"/>) … pageCount — ellipsis chỉ xuất hiện khi có khoảng thực sự bị bỏ.
    /// </summary>
    public static IReadOnlyList<int?> Build(int page, int pageCount)
    {
        pageCount = Math.Max(1, pageCount);
        page = Math.Clamp(page, 1, pageCount);

        if (pageCount <= ShowAllThreshold)
        {
            return Enumerable.Range(1, pageCount).Select(n => (int?)n).ToArray();
        }

        // Cửa sổ luôn nằm trong (1, pageCount) — hai đầu do nút First/Last đảm nhiệm.
        var start = Math.Max(2, page - Radius);
        var end = Math.Min(pageCount - 1, page + Radius);

        var result = new List<int?> { 1 };
        if (start > 2) result.Add(null);
        for (var n = start; n <= end; n++) result.Add(n);
        if (end < pageCount - 1) result.Add(null);
        result.Add(pageCount);
        return result;
    }
}
