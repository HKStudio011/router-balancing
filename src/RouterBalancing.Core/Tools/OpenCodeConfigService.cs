using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Tools;

/// <inheritdoc cref="IOpenCodeConfigService"/>
public sealed class OpenCodeConfigService : IOpenCodeConfigService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly IModelCapabilityReader _capabilities;

    /// <inheritdoc/>
    public OpenCodeConfigService(
        IDbContextFactory<RouterBalancingDbContext> db, IModelCapabilityReader capabilities)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(capabilities);
        _db = db;
        _capabilities = capabilities;
    }

    /// <inheritdoc/>
    public async Task<OpenCodeConfigPageData> GetPageDataAsync(CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var providers = await db.Providers.AsNoTracking()
            .Include(p => p.Models)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        var groups = new List<ToolsModelGroup>(providers.Count);
        var defaultSelectedIds = new List<long>();

        foreach (var provider in providers)
        {
            var isFreePreset = IsFreePreset(provider);
            var enabledModels = provider.Models
                .Where(m => m.Enabled)
                .OrderBy(m => m.Id)
                .ToList();

            // Preset OpenCode Free luôn có mặt kể cả khi tắt/rỗng: UI cần nhóm này để hiện CTA
            // "bật provider OpenCode Free" (§7.1.3 spec) — các nhóm rỗng khác ẩn cho gọn bảng.
            if (enabledModels.Count == 0 && !isFreePreset) continue;

            // Capability đọc MỘT lần cho cả nhóm qua reader (C-H) — không gọi reader
            // cho từng model khi render; ReadManyAsync = một query/bảng.
            var views = await _capabilities.ReadManyAsync(enabledModels.Select(m => m.Id).ToList(), ct);
            var rows = enabledModels
                .Select(m => ToRow(m, views.TryGetValue(m.Id, out var view) ? view : ModelCapabilityView.Empty))
                .ToList();

            groups.Add(new ToolsModelGroup(provider.Name, provider.Enabled, isFreePreset, rows));

            // C7: chỉ tick mặc định khi cả provider lẫn model đang bật; provider tắt vẫn tick tay được
            // (routing thật cần cả hai — tick mù sẽ sinh config trỏ tới model app không phục vụ được).
            if (isFreePreset && provider.Enabled)
            {
                defaultSelectedIds.AddRange(rows.Select(m => m.Id));
            }
        }

        return new OpenCodeConfigPageData(groups, defaultSelectedIds);
    }

    /// <inheritdoc/>
    public async Task<OpenCodeConfigResult> GenerateAsync(
        IReadOnlyList<long> selectedModelIds, string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selectedModelIds);
        ArgumentNullException.ThrowIfNull(baseUrl);

        using var db = _db.CreateDbContext();
        var models = await db.Models.AsNoTracking()
            .Include(m => m.Provider)
            .Where(m => selectedModelIds.Contains(m.Id))
            .ToListAsync(ct);

        // Capability của toàn bộ model tick được đọc MỘT lần (một query/bảng) — không
        // đọc cột capability cũ trên Models (C-H).
        var views = await _capabilities.ReadManyAsync(models.Select(m => m.Id).ToList(), ct);

        // Thứ tự chốt (§7.2): preset OpenCode Free trước, rồi Id tăng dần — bản thắng khi trùng ModelId
        // không phụ thuộc thứ tự tick của user (JSON object không cho phép key trùng).
        var ordered = models
            .OrderByDescending(m => IsFreePreset(m.Provider))
            .ThenBy(m => m.Id);

        var seenModelIds = new HashSet<string>(StringComparer.Ordinal);
        var picked = new List<OpenCodeConfigModel>(models.Count);
        var skipped = new List<string>();
        foreach (var model in ordered)
        {
            if (!seenModelIds.Add(model.ModelId))
            {
                skipped.Add(model.ModelId);
                continue;
            }

            var view = views.TryGetValue(model.Id, out var found) ? found : ModelCapabilityView.Empty;
            picked.Add(new OpenCodeConfigModel(
                model.ModelId, model.DisplayName, model.ContextWindow, model.MaxOutputTokens,
                view.Tools, view.Vision, view.Reasoning,
                view.Efforts, view.InputModalities, view.OutputModalities));
        }

        var json = OpenCodeConfigGenerator.Generate(baseUrl, apiKey, picked);
        return new OpenCodeConfigResult(json, skipped);
    }

    /// <summary>URL proxy cho opencode: port thật khi đang chạy, không thì port settings (§7.1.1 spec).</summary>
    /// <param name="proxyPort">Port proxy đang chạy; <see langword="null"/> = proxy chưa chạy.</param>
    /// <param name="settingsPort">Port cấu hình trong settings (dùng khi proxy chưa chạy).</param>
    public static string BuildBaseUrl(int? proxyPort, int settingsPort) =>
        $"http://127.0.0.1:{proxyPort ?? settingsPort}/v1";

    /// <summary>
    /// Preset OpenCode Free (C4): provider preset + BaseUrl canonical khớp catalog OpenCode —
    /// rename-safe (user đổi tên preset vẫn nhận ra nhờ <see cref="FreeProviderCatalog.FindByBaseUrl"/>).
    /// </summary>
    private static bool IsFreePreset(Provider? provider) =>
        provider is not null && provider.IsPreset
        && FreeProviderCatalog.FindByBaseUrl(provider.BaseUrl)?.DetectKind == FreeDetectKind.OpenCodeCatalog;

    private static ToolsModelRow ToRow(Model model, ModelCapabilityView view) =>
        new(model.Id, model.ModelId, model.DisplayName, model.ContextWindow,
            view.Tools, view.Reasoning, view.Vision, view.AudioInput, view.VideoInput,
            view.InputModalities, view.OutputModalities);
}
