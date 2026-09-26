using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
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

    /// <inheritdoc/>
    public ModelService(
        IDbContextFactory<RouterBalancingDbContext> db,
        ISecretProtector protector,
        IHttpClientFactory http,
        ILogService log)
    {
        _db = db;
        _protector = protector;
        _http = http;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<(int Added, int Skipped)> FetchFromProviderAsync(long providerId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers
            .Include(p => p.Models)
            .FirstOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new KeyNotFoundException($"Provider {providerId} not found.");

        var key = string.IsNullOrEmpty(provider.ApiKeyEncrypted)
            ? string.Empty
            : _protector.Unprotect(provider.ApiKeyEncrypted);

        using var request = ProviderRequestFactory.Create(provider, key);
        using var response = await _http.CreateClient(ProviderRequestFactory.HttpClientName)
            .SendAsync(request, ct);
        response.EnsureSuccessStatusCode(); // ném HttpRequestException → UI toast

        // Cả OpenAI lẫn Anthropic đều trả {"data":[{"id":...}]} — parse chung 1 shape;
        // field khác (type/display_name) bỏ qua, chỉ lấy id.
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var existing = provider.Models.Select(m => m.ModelId).ToHashSet(StringComparer.Ordinal);
        int added = 0, skipped = 0;

        if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl)) continue;
                var modelId = idEl.GetString();
                if (string.IsNullOrWhiteSpace(modelId)) continue;
                if (!existing.Add(modelId)) { skipped++; continue; }

                db.Models.Add(new Model { ProviderId = providerId, ModelId = modelId, IsManual = false });
                added++;
            }
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            _log.Info($"Fetched {added} models for provider {providerId}.");
        }

        return (added, skipped);
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
        foreach (var raw in modelIds)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue; // dòng rỗng — không tính skipped
            if (!existing.Add(trimmed)) { skipped++; continue; }

            db.Models.Add(new Model { ProviderId = providerId, ModelId = trimmed, IsManual = true });
            added++;
        }

        if (added > 0) await db.SaveChangesAsync(ct);
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
}
