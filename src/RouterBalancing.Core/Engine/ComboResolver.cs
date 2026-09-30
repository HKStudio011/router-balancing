using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Resolve model/combo theo spec §3.2: model id thật ưu tiên, không có thì tra Combo.Name
/// (items theo Position, recurse lồng nhau có cycle-guard).
/// </summary>
public sealed class ComboResolver(
    IDbContextFactory<RouterBalancingDbContext> db, ILogService log) : IComboResolver
{
    /// <inheritdoc/>
    public async Task<SelectionResult> ResolveAsync(string model, CancellationToken ct)
    {
        var candidates = await QueryModelCandidatesAsync(model, ct);
        var mode = ComboMode.RoundRobin;
        if (candidates.Count == 0)
        {
            var combo = await LoadComboAsync(model, ct);
            if (combo is null)
                return new SelectionFailure(model, ResolveFailure.NotFound);
            mode = combo.Mode;
            candidates = await ResolveComboAsync(combo, [combo.Id], ct);
        }

        return Finalize(model, candidates, mode);
    }

    /// <summary>
    /// "prefix/rest": nếu prefix là Identifier của bất kỳ provider nào → pin đúng provider
    /// (m.ModelId == rest). Không match / không có '/' → match toàn bộ ModelId như cũ (fallback spec §5).
    /// Query Identifier chỉ chạy khi model có '/' — chuỗi thường không tốn query thêm.
    /// </summary>
    private async Task<List<ModelCandidate>> QueryModelCandidatesAsync(string model, CancellationToken ct)
    {
        var separator = model.IndexOf('/');
        if (separator > 0)
        {
            var prefix = model[..separator];
            await using var context = await db.CreateDbContextAsync(ct);
            var isIdentifier = await context.Providers.AsNoTracking()
                .AnyAsync(p => p.Identifier == prefix, ct);
            if (isIdentifier)
            {
                var rest = model[(separator + 1)..];
                return await QueryCandidatesAsync(
                    m => m.Provider!.Identifier == prefix && m.ModelId == rest, ct);
            }
        }

        return await QueryCandidatesAsync(m => m.ModelId == model, ct);
    }

    /// <summary>Query model enabled + provider enabled, Include Accounts — giống 3A nhưng trả tất cả candidate.</summary>
    private async Task<List<ModelCandidate>> QueryCandidatesAsync(
        System.Linq.Expressions.Expression<Func<Model, bool>> predicate, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var models = await context.Models.AsNoTracking()
            .Where(m => m.Enabled && m.Provider!.Enabled)
            .Where(predicate)
            .Include(m => m.Provider!)
            .ThenInclude(p => p.Accounts)
            .OrderBy(m => m.Provider!.Id)
            .ThenBy(m => m.ModelId)
            .ToListAsync(ct);
        // Map sau khi materialize — Include bị EF bỏ qua nếu có Select projection (Accounts sẽ rỗng)
        return models.Select(m => new ModelCandidate(m.Provider!, m)).ToList();
    }

    private async Task<Combo?> LoadComboAsync(string name, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.Combos.AsNoTracking()
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Name == name, ct);
    }

    private async Task<List<ModelCandidate>> ResolveComboAsync(
        Combo combo, HashSet<long> path, CancellationToken ct)
    {
        var result = new List<ModelCandidate>();
        foreach (var item in combo.Items.OrderBy(i => i.Position))
        {
            if (item.TargetModelId is long modelKey)
            {
                result.AddRange(await QueryCandidatesAsync(m => m.Id == modelKey, ct));
            }
            else if (item.TargetComboId is long comboKey)
            {
                if (path.Contains(comboKey))
                {
                    // Validate lúc lưu đã chặn cycle — đây là defense, bỏ qua item gây vòng (spec §3.2)
                    log.Warn($"Combo {combo.Name} có vòng lặp - bỏ qua item {item.Id}.", LogCategory.App);
                    continue;
                }
                await using var context = await db.CreateDbContextAsync(ct);
                var child = await context.Combos.AsNoTracking()
                    .Include(c => c.Items)
                    .FirstOrDefaultAsync(c => c.Id == comboKey, ct);
                if (child is null)
                    continue;
                path.Add(comboKey);
                result.AddRange(await ResolveComboAsync(child, path, ct));
                path.Remove(comboKey); // path-based: diamond không phải cycle
            }
        }
        return result;
    }

    private static SelectionResult Finalize(string model, List<ModelCandidate> candidates, ComboMode mode)
    {
        if (candidates.Count == 0)
            return new SelectionFailure(model, ResolveFailure.NotFound);

        var deduped = candidates
            .DistinctBy(c => (c.Provider.Id, c.Model.ModelId))
            .ToList();
        // RR cần thứ tự ổn định (ProviderId, ModelId); Fallback giữ nguyên thứ tự Position (spec §3.3)
        if (mode == ComboMode.RoundRobin)
            deduped = deduped.OrderBy(c => c.Provider.Id).ThenBy(c => c.Model.ModelId).ToList();

        var openAi = deduped.Where(c => c.Provider.Type == ProviderType.OpenAI).ToList();
        if (openAi.Count > 0)
            return new SelectionSuccess(openAi, mode);

        // Chỉ còn candidate Anthropic → 503 (giữ semantics 3A, không rơi xuống 404)
        return new SelectionFailure(model, ResolveFailure.AnthropicNotSupported);
    }
}
