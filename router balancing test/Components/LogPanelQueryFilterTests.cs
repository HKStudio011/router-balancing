namespace router_balancing_test.Components;

/// <summary>
/// Guard cho deep-link từ notification lỗi (spec error-notify-client-name §2.4): notification
/// điều hướng tới <c>/logs?severity=Error</c>, nên <c>LogPanel</c> phải (a) khai báo
/// <c>SupplyParameterFromQuery</c> cho severity/category, (b) nạp giá trị query TRƯỚC lần
/// <c>Load()</c> đầu tiên — nếu Load chạy trước thì bảng hiện ra chưa lọc đúng thứ mà
/// notification đã hứa với user, và (c) re-apply khi page đã mở sẵn (Blazor tái dùng instance).
/// Test project không reference project app MAUI nên component không khởi tạo được trong test —
/// đọc file <c>.razor</c> như text (cùng pattern <see cref="RazorParameterEntityTests"/>).
/// </summary>
public class LogPanelQueryFilterTests
{
    private const string SeverityAttribute = "[SupplyParameterFromQuery(Name = \"severity\")]";
    private const string CategoryAttribute = "[SupplyParameterFromQuery(Name = \"category\")]";

    [Fact]
    public void LogPanel_SuppliesSeverityFromQuery()
    {
        Assert.Contains(SeverityAttribute, ReadLogPanel(), StringComparison.Ordinal);
    }

    [Fact]
    public void LogPanel_SuppliesCategoryFromQuery()
    {
        Assert.Contains(CategoryAttribute, ReadLogPanel(), StringComparison.Ordinal);
    }

    [Fact]
    public void LogPanel_RouteIsStillLogs()
    {
        // Deep-link phụ thuộc route — đổi route mà quên sửa ErrorNotifier/AppNavigation thì
        // bấm notification mở ra trang trống.
        Assert.Contains("@page \"/logs\"", ReadLogPanel(), StringComparison.Ordinal);
    }

    [Fact]
    public void LogPanel_AppliesQueryFilterBeforeFirstLoad()
    {
        var body = MethodBody("protected override void OnInitialized()");

        var queryIndex = body.IndexOf("SeverityQuery", StringComparison.Ordinal);
        var loadIndex = body.IndexOf("Load()", StringComparison.Ordinal);

        Assert.True(queryIndex >= 0, "OnInitialized không tham chiếu SeverityQuery — query filter không được nạp ở lần render đầu.");
        Assert.True(loadIndex >= 0, "OnInitialized không gọi Load() — page không tải dữ liệu ban đầu.");
        Assert.True(
            queryIndex < loadIndex,
            "OnInitialized phải gán filter từ query TRƯỚC Load() đầu tiên, nếu không bảng hiện ra chưa lọc.");
    }

    [Fact]
    public void LogPanel_ReAppliesQueryFilterOnParametersSet()
    {
        var body = MethodBody("protected override void OnParametersSet()");

        Assert.Contains("SeverityQuery", body, StringComparison.Ordinal);
        Assert.Contains("CategoryQuery", body, StringComparison.Ordinal);
        Assert.Contains("Load()", body, StringComparison.Ordinal);
        // Ruling Task 9: OnParametersSet fires on every parameter change — phải có guard so sánh
        // với filter đang áp dụng, nếu không mỗi lần re-render sẽ Load() lại từ DB.
        Assert.Contains("_appliedSeverityQuery", body, StringComparison.Ordinal);
        // StateHasChanged thủ công ở đây là render trùng lần do framework đã lên lịch sau SetParametersAsync.
        Assert.DoesNotContain("StateHasChanged", body, StringComparison.Ordinal);
    }

    private static string ReadLogPanel() => File.ReadAllText(LogPanelPath());

    /// <summary>
    /// Tách thân hàm của <paramref name="signature"/> bằng cách đếm ngoặc nhọn — đủ cho
    /// method không có nested lambda nhiều ngoặc; page chỉ có method đơn giản nên không cần parser.
    /// Comment <c>//</c> bị cắt trước khi trả: các assert tìm chuỗi (Load(), StateHasChanged)
    /// phải đọc code thật, không đọc comment giải thích.
    /// </summary>
    private static string MethodBody(string signature)
    {
        var content = ReadLogPanel();
        var start = content.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Không tìm thấy “{signature}” trong LogPanel.razor — page phải có hook này cho deep-link.");

        var open = content.IndexOf('{', start);
        Assert.True(open >= 0, $"Thân của “{signature}” không có ngoặc mở.");

        var depth = 0;
        for (var i = open; i < content.Length; i++)
        {
            if (content[i] == '{')
            {
                depth++;
            }
            else if (content[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return StripLineComments(content.Substring(open, i - open + 1));
                }
            }
        }

        throw new InvalidOperationException($"Thân của “{signature}” không đóng ngoặc cân bằng.");
    }

    private static string StripLineComments(string body) => string.Join(
        "\n",
        body
            .Split('\n')
            .Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment < 0 ? line : line.Substring(0, comment);
            }));

    private static string LogPanelPath()
    {
        var path = Path.Combine(
            FindRepoRoot(), "router-balancing", "Components", "Pages", "LogPanel.razor");
        return File.Exists(path)
            ? path
            : throw new InvalidOperationException($"Không tìm thấy LogPanel tại {path}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "router-balancing.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException($"Không tìm thấy repo root (router-balancing.slnx) từ {AppContext.BaseDirectory}");
    }
}
