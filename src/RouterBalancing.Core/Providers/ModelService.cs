using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Providers;

/// <inheritdoc cref="IModelService"/>
public sealed class ModelService : IModelService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;
    private readonly IHttpClientFactory _http;
    private readonly ILogService _log;
    private readonly IModelMetadataService _metadata;
    private readonly IModelCapabilityWriter _capabilities;

    /// <inheritdoc/>
    public ModelService(
        IDbContextFactory<RouterBalancingDbContext> db,
        ISecretProtector protector,
        IHttpClientFactory http,
        ILogService log,
        IModelMetadataService metadata,
        IModelCapabilityWriter capabilities)
    {
        _db = db;
        _protector = protector;
        _http = http;
        _log = log;
        _metadata = metadata;
        _capabilities = capabilities;
    }

    /// <inheritdoc/>
    public async Task<(int Added, int Skipped, int Filled)> FetchFromProviderAsync(
        long providerId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers
            .Include(p => p.Models)
            .Include(p => p.Accounts)
            .Include(p => p.ProviderProxies) // resolver đọc junction — thiếu thì provider có proxy cũng resolve Direct
            .FirstOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new KeyNotFoundException($"Provider {providerId} not found.");

        var key = ProviderKeyResolver.ResolveFirstEnabledKey(provider, _protector) ?? string.Empty;

        // Probe đi theo assignment của provider (pattern TestConnectionAsync, D4): thiếu
        // ProxyTarget → ProxyHealthHandler rơi về global pool (D7) → provider không gán
        // proxy vẫn bị gửi qua SOCKS proxy của pool (bug 2026-10-06).
        using var request = ProviderRequestFactory.Create(provider, key);
        HttpResponseMessage response;
        ProxyTarget.Current.Value = new ProxyTarget(provider, null);
        try
        {
            response = await _http.CreateClient(ProviderRequestFactory.HttpClientName)
                .SendAsync(request, ct);
        }
        finally
        {
            ProxyTarget.Current.Value = null;
        }

        using (response)
        {
            response.EnsureSuccessStatusCode(); // ném HttpRequestException → UI toast

            // Cả OpenAI lẫn Anthropic đều trả {"data":[{"id":...}]} — parse chung 1 shape;
            // field khác (type/display_name) bỏ qua, chỉ lấy id.
            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            var existing = provider.Models.Select(m => m.ModelId).ToHashSet(StringComparer.Ordinal);
            int added = 0, skipped = 0;
            // Gom entities vừa Add để fill metadata SAU khi SaveChanges gán Id —
            // không query lại theo IsManual/Local (dễ trúng entity cũ do Include đã load).
            var created = new List<Model>();

            if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idEl)) continue;
                    var modelId = idEl.GetString();
                    if (string.IsNullOrWhiteSpace(modelId)) continue;
                    if (!existing.Add(modelId)) { skipped++; continue; }

                    var newModel = new Model { ProviderId = providerId, ModelId = modelId, IsManual = false };
                    db.Models.Add(newModel);
                    created.Add(newModel);
                    added++;
                }
            }

            if (created.Count > 0)
            {
                await db.SaveChangesAsync(ct);
            }

            // R-T8-1: FillMissing chạy cả khi không thêm model nào — nút Fetch retry các
            // model cũ đang rỗng, và đây cũng là nơi duy nhất tính "Filled" cho UI.
            // (TryFillAsync từng model ở đây là thừa: fill-missing đã bao gồm model mới.)
            var filled = await _metadata.FillMissingAsync(providerId, ct);
            _log.Info($"Fetched {added} models for provider {providerId}; filled metadata for {filled}.");
            return (added, skipped, filled);
        }
    }

    /// <inheritdoc/>
    public async Task<Model> AddManualAsync(long providerId, string modelId, CancellationToken ct = default)
    {
        var trimmed = modelId.Trim();
        ArgumentException.ThrowIfNullOrWhiteSpace(trimmed);

        using var db = _db.CreateDbContext();
        if (!await db.Providers.AnyAsync(p => p.Id == providerId, ct))
        {
            throw new KeyNotFoundException($"Provider {providerId} not found.");
        }
        if (await db.Models.AnyAsync(m => m.ProviderId == providerId && m.ModelId == trimmed, ct))
        {
            throw new InvalidOperationException($"Model '{trimmed}' already exists in provider {providerId}.");
        }

        var model = new Model { ProviderId = providerId, ModelId = trimmed, IsManual = true };
        db.Models.Add(model);
        await db.SaveChangesAsync(ct);
        // R-T8-5: row mới luôn IsEmpty theo construction — TryFill là đủ; không thêm
        // dependency reader vào ModelService cho đường thêm tay.
        await _metadata.TryFillAsync(model.Id, ct);
        return model;
    }

    /// <inheritdoc/>
    public async Task<(int Added, int Skipped)> AddBulkAsync(
        long providerId, IReadOnlyList<string> modelIds, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        if (!await db.Providers.AnyAsync(p => p.Id == providerId, ct))
        {
            throw new KeyNotFoundException($"Provider {providerId} not found.");
        }

        var existing = (await db.Models
            .Where(m => m.ProviderId == providerId)
            .Select(m => m.ModelId)
            .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        int added = 0, skipped = 0;
        // Gom entities vừa Add để fill SAU SaveChanges (xem FetchFromProviderAsync).
        var created = new List<Model>();
        foreach (var raw in modelIds)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue; // dòng rỗng — không tính skipped
            if (!existing.Add(trimmed)) { skipped++; continue; }

            var newModel = new Model { ProviderId = providerId, ModelId = trimmed, IsManual = true };
            db.Models.Add(newModel);
            created.Add(newModel);
            added++;
        }

        if (created.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (var entity in created)
            {
                // R-T8-5: mỗi row mới IsEmpty theo construction — fill trực tiếp, không
                // cần query reader để xác định candidate.
                await _metadata.TryFillAsync(entity.Id, ct);
            }
        }
        return (added, skipped);
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(long modelId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var model = await db.Models.FirstOrDefaultAsync(m => m.Id == modelId, ct)
            ?? throw new KeyNotFoundException($"Model {modelId} not found.");
        db.Models.Remove(model);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<int> RemoveAllAsync(long providerId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var models = await db.Models.Where(m => m.ProviderId == providerId).ToListAsync(ct);
        db.Models.RemoveRange(models);
        await db.SaveChangesAsync(ct);
        return models.Count;
    }

    /// <inheritdoc/>
    public async Task SetEnabledAsync(long modelId, bool enabled, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var model = await db.Models.FirstOrDefaultAsync(m => m.Id == modelId, ct)
            ?? throw new KeyNotFoundException($"Model {modelId} not found.");
        model.Enabled = enabled;
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task SetAllEnabledAsync(long providerId, bool enabled, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var models = await db.Models.Where(m => m.ProviderId == providerId).ToListAsync(ct);
        foreach (var model in models)
        {
            model.Enabled = enabled;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task SetCapabilitiesAsync(
        long modelId, int? contextWindow, int? maxOutputTokens,
        CapabilityPatch patch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        // Relational pattern: null không match → null hợp lệ (clear)
        if (contextWindow is < 1 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(contextWindow), contextWindow,
                "Context window phải trong khoảng 1..10.000.000.");
        }
        if (maxOutputTokens is < 1 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens), maxOutputTokens,
                "Max output tokens phải trong khoảng 1..10.000.000.");
        }

        using var db = _db.CreateDbContext();
        var model = await db.Models.FirstOrDefaultAsync(m => m.Id == modelId, ct)
            ?? throw new KeyNotFoundException($"Model {modelId} not found.");

        // Fact trước, scalar sau: writer tự mở transaction. Hai bước không nguyên tử
        // (xem XML doc) — nhưng capability là phần user vừa sửa, không được mất nếu
        // bước scalar gặp lỗi DB hiếm gặp.
        await _capabilities.ApplyUserEditAsync(modelId, patch, ct);
        model.ContextWindow = contextWindow;
        model.MaxOutputTokens = maxOutputTokens;
        await db.SaveChangesAsync(ct);
    }
}
