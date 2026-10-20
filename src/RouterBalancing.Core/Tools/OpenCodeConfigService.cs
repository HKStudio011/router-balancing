using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Combos;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Tools;

/// <inheritdoc cref="IOpenCodeConfigService"/>
public sealed class OpenCodeConfigService : IOpenCodeConfigService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly IModelCapabilityReader _capabilities;
    private readonly IComboCapabilityCalculator _comboCapabilities;

    /// <inheritdoc/>
    public OpenCodeConfigService(
        IDbContextFactory<RouterBalancingDbContext> db,
        IModelCapabilityReader capabilities,
        IComboCapabilityCalculator comboCapabilities)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(comboCapabilities);
        _db = db;
        _capabilities = capabilities;
        _comboCapabilities = comboCapabilities;
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

            groups.Add(new ToolsModelGroup(provider.Id, provider.Name, provider.Enabled, isFreePreset, rows));

            // C7: chỉ tick mặc định khi cả provider lẫn model đang bật; provider tắt vẫn tick tay được
            // (routing thật cần cả hai — tick mù sẽ sinh config trỏ tới model app không phục vụ được).
            if (isFreePreset && provider.Enabled)
            {
                defaultSelectedIds.AddRange(rows.Select(m => m.Id));
            }
        }

        var combos = await LoadComboRowsAsync(db, ct);
        return new OpenCodeConfigPageData(groups, combos, defaultSelectedIds);
    }

    /// <inheritdoc/>
    public async Task<OpenCodeConfigResult> GenerateAsync(
        IReadOnlyList<long> selectedModelIds, IReadOnlyList<long> selectedComboIds,
        string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        var (provider, skipped) = await BuildProviderAsync(selectedModelIds, selectedComboIds, baseUrl, apiKey, ct);
        return new OpenCodeConfigResult(OpenCodeConfigGenerator.GenerateFromProvider(provider), skipped);
    }

    /// <summary>
    /// Dựng object provider <c>router-balancing</c> + danh sách key bị bỏ (trùng) — dùng chung
    /// cho preview (<see cref="GenerateAsync"/>) và ghi file (WriteAsync).
    /// </summary>
    private async Task<(System.Text.Json.Nodes.JsonObject Provider, IReadOnlyList<string> Skipped)> BuildProviderAsync(
        IReadOnlyList<long> selectedModelIds, IReadOnlyList<long> selectedComboIds,
        string baseUrl, string? apiKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(selectedModelIds);
        ArgumentNullException.ThrowIfNull(selectedComboIds);
        ArgumentNullException.ThrowIfNull(baseUrl);

        using var db = _db.CreateDbContext();
        var models = await db.Models.AsNoTracking()
            .Include(m => m.Provider)
            .Where(m => selectedModelIds.Contains(m.Id))
            .ToListAsync(ct);

        // Capability của toàn bộ model tick được đọc MỘT lần (một query/bảng) — không
        // đọc cột capability cũ trên Models (C-H).
        var views = await _capabilities.ReadManyAsync(models.Select(m => m.Id).ToList(), ct);

        // Thứ tự chốt (§7.2): preset OpenCode Free trước, rồi Id tăng dần — bản thắng khi trùng key
        // không phụ thuộc thứ tự tick của user (JSON object không cho phép key trùng).
        var ordered = models
            .OrderByDescending(m => IsFreePreset(m.Provider))
            .ThenBy(m => m.Id);

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var picked = new List<OpenCodeConfigModel>(models.Count);
        var skipped = new List<string>();
        foreach (var model in ordered)
        {
            // C-A: key luôn "<Identifier>/<ModelId>"; Identifier null (hàng cũ chưa backfill) → id trần.
            var key = BuildConfigKey(model.Provider, model.ModelId);
            if (!seenKeys.Add(key))
            {
                skipped.Add(key);
                continue;
            }

            var view = views.TryGetValue(model.Id, out var found) ? found : ModelCapabilityView.Empty;
            picked.Add(new OpenCodeConfigModel(
                model.ModelId, model.DisplayName, model.ContextWindow, model.MaxOutputTokens,
                view.Tools, view.Vision, view.Reasoning,
                view.Efforts, view.InputModalities, view.OutputModalities,
                Key: key));
        }

        // U12: combo = một entry key tên combo; capability là giao an toàn của member (C-L).
        var selectedCombos = selectedComboIds.Count == 0
            ? []
            : await db.Combos.AsNoTracking()
                .Where(c => selectedComboIds.Contains(c.Id))
                .OrderBy(c => c.Name)
                .ToListAsync(ct);

        var comboEntries = new List<OpenCodeConfigCombo>(selectedCombos.Count);
        if (selectedCombos.Count > 0)
        {
            var comboCapabilities = await _comboCapabilities.ComputeAllAsync(ct);
            foreach (var combo in selectedCombos)
            {
                if (!seenKeys.Add(combo.Name))
                {
                    skipped.Add(combo.Name);
                    continue;
                }

                var caps = comboCapabilities.TryGetValue(combo.Id, out var found)
                    ? found
                    : new ComboCapabilities(null, null, null, null, null);
                comboEntries.Add(new OpenCodeConfigCombo(
                    combo.Name, caps.ContextWindow, caps.Tools, caps.Vision, caps.Thinking,
                    caps.InputModalities, caps.OutputModalities));
            }
        }

        return (OpenCodeConfigGenerator.BuildProviderObject(baseUrl, apiKey, picked, comboEntries), skipped);
    }

    private async Task<IReadOnlyList<ToolsComboRow>> LoadComboRowsAsync(
        RouterBalancingDbContext db, CancellationToken ct)
    {
        var combos = await db.Combos.AsNoTracking()
            .Include(c => c.Items)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
        if (combos.Count == 0) return [];

        var capabilities = await _comboCapabilities.ComputeAllAsync(ct);
        return combos.Select(combo => ToComboRow(
            combo,
            capabilities.TryGetValue(combo.Id, out var found)
                ? found
                : new ComboCapabilities(null, null, null, null, null))).ToArray();
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

    private static string BuildConfigKey(Provider? provider, string modelId) =>
        string.IsNullOrEmpty(provider?.Identifier) ? modelId : $"{provider.Identifier}/{modelId}";

    private static ToolsModelRow ToRow(Model model, ModelCapabilityView view) =>
        new(model.Id, model.ModelId, model.DisplayName, model.ContextWindow,
            view.Tools, view.Reasoning, view.Vision, view.AudioInput, view.VideoInput,
            view.InputModalities, view.OutputModalities);

    private static ToolsComboRow ToComboRow(Combo combo, ComboCapabilities caps) =>
        new(combo.Id, combo.Name, combo.Mode, combo.Items.Count,
            caps.Tools, caps.Thinking, caps.Vision, caps.Audio, caps.Video,
            caps.ContextWindow, caps.InputModalities, caps.OutputModalities);
}
