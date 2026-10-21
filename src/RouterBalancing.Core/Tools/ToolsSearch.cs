namespace RouterBalancing.Core.Tools;

/// <summary>
/// Lọc tìm kiếm trang Tools (spec §9.3): khớp không phân biệt hoa thường trên tên hiển thị,
/// ModelId và tên combo; query rỗng/whitespace = khớp tất cả.
/// </summary>
public static class ToolsSearch
{
    /// <summary>Model có khớp query không — xét cả <see cref="ToolsModelRow.DisplayName"/> lẫn <see cref="ToolsModelRow.ModelId"/>.</summary>
    public static bool MatchesModel(ToolsModelRow model, string? query)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrWhiteSpace(query)) return true;

        var needle = query.Trim();
        return Contains(model.DisplayName) || Contains(model.ModelId);

        bool Contains(string? value) =>
            value is not null && value.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Combo có khớp query theo tên không (tên combo cũng là key config).</summary>
    public static bool MatchesCombo(ToolsComboRow combo, string? query)
    {
        ArgumentNullException.ThrowIfNull(combo);
        return string.IsNullOrWhiteSpace(query)
            || combo.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
