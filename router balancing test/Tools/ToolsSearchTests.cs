using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Tools;

namespace router_balancing_test.Tools;

/// <summary>Lọc tìm kiếm trang Tools: không phân biệt hoa thường, khớp tên hiển thị + ModelId + tên combo.</summary>
public class ToolsSearchTests
{
    private static ToolsModelRow Model(string modelId, string? displayName = null) =>
        new(1, modelId, displayName, null, null, null, null, null, null, null, null);

    private static ToolsComboRow Combo(string name) =>
        new(2, name, ComboMode.RoundRobin, 1, null, null, null, null, null, null, null, null);

    [Fact]
    public void MatchesModel_EmptyQuery_True()
    {
        Assert.True(ToolsSearch.MatchesModel(Model("m1"), ""));
        Assert.True(ToolsSearch.MatchesModel(Model("m1"), null));
        Assert.True(ToolsSearch.MatchesModel(Model("m1"), "   "));
    }

    [Fact]
    public void MatchesModel_DisplayNameCaseInsensitive()
    {
        Assert.True(ToolsSearch.MatchesModel(Model("m1", "Beam-Free"), "beam"));
    }

    [Fact]
    public void MatchesModel_ModelIdMatchesWhenDisplayNameSet()
    {
        // DisplayName có thể khác ModelId — search phải khớp cả hai (spec §9.3).
        Assert.True(ToolsSearch.MatchesModel(Model("beam-free", "Tên khác"), "BEAM"));
    }

    [Fact]
    public void MatchesModel_NoMatch_False()
    {
        Assert.False(ToolsSearch.MatchesModel(Model("m1", "Alpha"), "beta"));
    }

    [Fact]
    public void MatchesCombo_CaseInsensitiveName()
    {
        Assert.True(ToolsSearch.MatchesCombo(Combo("Unsloth-Studio"), "studio"));
    }

    [Fact]
    public void MatchesCombo_EmptyQuery_True()
    {
        Assert.True(ToolsSearch.MatchesCombo(Combo("x"), "  "));
    }
}
