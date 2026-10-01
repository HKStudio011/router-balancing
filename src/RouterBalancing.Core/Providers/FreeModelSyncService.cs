using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Providers;

/// <inheritdoc cref="IFreeModelSyncService"/>
public sealed class FreeModelSyncService : IFreeModelSyncService
{
    /// <summary>Tên named HttpClient — timeout 30s (list model lớn), đăng ký trong MauiProgram.</summary>
    public const string HttpClientName = "free-model-sync";

    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;
    private readonly IHttpClientFactory _http;
    private readonly ILogService _log;

    /// <inheritdoc/>
    public FreeModelSyncService(
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
    public async Task<int> SyncProviderAsync(long providerId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers
            .Include(p => p.Models)
            .Include(p => p.Accounts)
            .FirstOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new FreeModelSyncException($"Provider {providerId} not found.");
        if (!provider.IsPreset)
        {
            throw new FreeModelSyncException($"Provider {providerId} is not a free preset.");
        }

        // Match catalog theo Name (chốt S1, spec §6.2.2): user đổi tên preset → mất link sync
        var entry = FreeProviderCatalog.FindByDisplayName(provider.Name)
            ?? throw new FreeModelSyncException(
                $"Provider '{provider.Name}' is not in the free catalog (renamed?).");

        // Fetch (spec §6.1): key optional (4 endpoint public) — có account thì gắn Authorization
        var key = ProviderKeyResolver.ResolveFirstEnabledKey(provider, _protector) ?? string.Empty;
        using var request = ProviderRequestFactory.Create(provider, key);
        using var response = await _http.CreateClient(HttpClientName).SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            // Non-200 → ném TRƯỚC khi đọc body; không đụng DB (§6.2.3)
            throw new FreeModelSyncException(
                $"{provider.Name} returned HTTP {(int)response.StatusCode} for model list.");
        }

        var json = await response.Content.ReadAsStringAsync(ct);

        IReadOnlyList<FreeModelInfo> free;
        try
        {
            free = FreeModelDetector.Parse(json, entry.DetectKind);
        }
        catch (JsonException ex)
        {
            throw new FreeModelSyncException(
                $"Model list response from {provider.Name} is not valid JSON.", ex);
        }

        // Guard §6.2.5: API trả rỗng bất thường → KHÔNG bao giờ xoá sạch list cũ
        if (free.Count == 0)
        {
            throw new FreeModelSyncException(
                $"{provider.Name} returned 0 free models — keeping existing list.");
        }

        // ===== Merge (§6.2.6) — 1 SaveChanges duy nhất = atomic per provider =====
        var freeIds = free.Select(f => f.ModelId).ToHashSet(StringComparer.Ordinal);

        // Fetched (!IsManual) biến mất khỏi F → delete; manual giữ vĩnh viễn
        var stale = provider.Models.Where(m => !m.IsManual && !freeIds.Contains(m.ModelId)).ToList();
        if (stale.Count > 0)
        {
            db.Models.RemoveRange(stale);
        }

        var byId = provider.Models.ToDictionary(m => m.ModelId, StringComparer.Ordinal);
        foreach (var info in free)
        {
            if (byId.TryGetValue(info.ModelId, out var model))
            {
                // Manual của user: giữ nguyên kể cả metadata (§6.2.6)
                if (model.IsManual) continue;
                if (info.DisplayName is not null) model.DisplayName = info.DisplayName;
                if (info.ContextWindow is not null) model.ContextWindow = info.ContextWindow;
            }
            else
            {
                db.Models.Add(new Model
                {
                    ProviderId = provider.Id,
                    ModelId = info.ModelId,
                    IsManual = false,
                    Enabled = true,
                    DisplayName = info.DisplayName,
                    ContextWindow = info.ContextWindow,
                });
            }
        }

        provider.LastModelSyncAt = DateTimeOffset.UtcNow;
        provider.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _log.Info($"Synced {free.Count} free models for provider {providerId}.");
        return free.Count;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<(long ProviderId, int ModelCount)>> SyncAllEnabledAsync(
        CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var ids = await db.Providers.AsNoTracking()
            .Where(p => p.IsPreset && p.Enabled)
            .Select(p => p.Id)
            .ToListAsync(ct);

        var results = new List<(long ProviderId, int ModelCount)>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                results.Add((id, await SyncProviderAsync(id, ct)));
            }
            catch (OperationCanceledException)
            {
                throw; // app thoát giữa chừng — không nuốt
            }
            catch (FreeModelSyncException ex)
            {
                // 1 provider lỗi (§7/§9): log warning + skip, các provider còn lại vẫn sync
                _log.Warn($"Skipping provider {id} in free model sync: {ex.Message}");
            }
            catch (Exception ex)
            {
                _log.Error($"Unexpected error syncing free models for provider {id}.", ex);
            }
        }
        return results;
    }
}
