using RouterBalancing.Core.Tools;

namespace router_balancing_test.Tools;

/// <summary>
/// Resolver quyết định lựa chọn tick khi mở trang Tools (spec §9.1): có bản ghi nhớ thì tôn trọng
/// (kể cả rỗng), chưa có thì dùng mặc định C7 — luôn lọc id đã bị xoá khỏi DB.
/// </summary>
public class ToolsSelectionResolverTests
{
    [Fact]
    public void Resolve_NoStoredSelection_UsesDefaultsFilteredByAvailableIds()
    {
        var selection = ToolsSelectionResolver.Resolve(
            stored: null,
            availableModelIds: [1, 2, 3],
            availableComboIds: [10],
            defaultModelIds: [2, 3, 99]); // 99 đã bị xoá

        Assert.Equal(new long[] { 2, 3 }, selection.ModelIds);
        Assert.Empty(selection.ComboIds);
    }

    [Fact]
    public void Resolve_StoredSelection_DropsStaleModelAndComboIds()
    {
        var stored = new ToolsSelection([1, 99], [10, 88]);

        var selection = ToolsSelectionResolver.Resolve(
            stored, availableModelIds: [1, 2], availableComboIds: [10, 11], defaultModelIds: [2]);

        Assert.Equal(new long[] { 1 }, selection.ModelIds);
        Assert.Equal(new long[] { 10 }, selection.ComboIds);
    }

    [Fact]
    public void Resolve_StoredEmptySelection_StaysEmptyNotDefaults()
    {
        // RF-5: user đã bỏ chọn hết — mở lại trang KHÔNG được tick mặc định lại (khác "chưa có bản ghi").
        var stored = new ToolsSelection([], []);

        var selection = ToolsSelectionResolver.Resolve(
            stored, availableModelIds: [1, 2], availableComboIds: [], defaultModelIds: [1, 2]);

        Assert.Empty(selection.ModelIds);
        Assert.Empty(selection.ComboIds);
    }

    [Fact]
    public void Resolve_StoredAllStale_ReturnsEmpty()
    {
        var stored = new ToolsSelection([99], [88]);

        var selection = ToolsSelectionResolver.Resolve(
            stored, availableModelIds: [1], availableComboIds: [10], defaultModelIds: [1]);

        Assert.Empty(selection.ModelIds);
        Assert.Empty(selection.ComboIds);
    }
}
