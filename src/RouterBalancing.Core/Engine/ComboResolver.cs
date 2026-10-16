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
        if (candidates.Count > 0)
            return FinalizeModel(model, candidates);

        var combo = await LoadComboAsync(model, ct);
        if (combo is null)
            return new SelectionFailure(model, ResolveFailure.NotFound);

        // Composition: trả CÂY giữ mode từng cấp (TargetComboId → ComboUnit với Mode của chính nó)
        // thay vì flatten bỏ mode con (bug: FB cha luôn pick lá đầu bất kể mode con).
        var nodes = await ResolveComboAsync(combo, [combo.Id], ct);
        return FinalizeCombo(model, new ComboUnit(combo.Name, combo.Mode, nodes));
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

    /// <summary>
    /// Query model enabled + provider enabled, Include Accounts + junction proxy —
    /// giống 3A nhưng trả tất cả candidate.
    /// </summary>
    private async Task<List<ModelCandidate>> QueryCandidatesAsync(
        System.Linq.Expressions.Expression<Func<Model, bool>> predicate, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var models = await context.Models.AsNoTracking()
            .Where(m => m.Enabled && m.Provider!.Enabled)
            .Where(predicate)
            .Include(m => m.Provider!)
            .ThenInclude(p => p.Accounts)
            .ThenInclude(a => a.AccountProxies)
            // ProxySelectionResolver đọc từ entity đã load (spec §6) — thiếu junction
            // thì ProviderProxies/AccountProxies luôn rỗng và mọi request resolve ra Direct.
            .Include(m => m.Provider!)
            .ThenInclude(p => p.ProviderProxies)
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

    private async Task<List<SelectionNode>> ResolveComboAsync(
        Combo combo, HashSet<long> path, CancellationToken ct)
    {
        var nodes = new List<SelectionNode>();
        foreach (var item in combo.Items.OrderBy(i => i.Position))
        {
            if (item.TargetModelId is long modelKey)
            {
                foreach (var candidate in await QueryCandidatesAsync(m => m.Id == modelKey, ct))
                    nodes.Add(new CandidateLeaf(candidate));
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
                var children = await ResolveComboAsync(child, path, ct);
                path.Remove(comboKey); // path-based: diamond không phải cycle
                // Mode của CHÍNH combo con — không dùng mode top-level (composition)
                nodes.Add(new ComboUnit(child.Name, child.Mode, children));
            }
        }
        return nodes;
    }

    /// <summary>Finalize nhánh model id — giữ nguyên semantics: dedup, RR sort, filter OpenAI.</summary>
    private static SelectionResult FinalizeModel(string model, List<ModelCandidate> candidates)
    {
        var deduped = candidates
            .DistinctBy(c => (c.Provider.Id, c.Model.ModelId))
            .ToList();
        // RR cần thứ tự ổn định (ProviderId, ModelId)
        deduped = deduped.OrderBy(c => c.Provider.Id).ThenBy(c => c.Model.ModelId).ToList();

        var openAi = deduped.Where(c => c.Provider.Type == ProviderType.OpenAI).ToList();
        if (openAi.Count > 0)
            return new SelectionSuccess(openAi, ComboMode.RoundRobin);

        // Chỉ còn candidate Anthropic → 503 (giữ semantics 3A, không rơi xuống 404)
        return new SelectionFailure(model, ResolveFailure.AnthropicNotSupported);
    }

    /// <summary>
    /// Finalize nhánh combo: prune lá non-OpenAI + unit rỗng (bottom-up), flatten-dedup cho
    /// field <see cref="SelectionSuccess.Candidates"/> (backward: exhaustion-check/attemptBudget/tests),
    /// giữ <paramref name="root"/> đã prune để selector đi đệ quy mode từng cấp.
    /// </summary>
    private static SelectionResult FinalizeCombo(string model, ComboUnit root)
    {
        var hadCandidates = CountLeaves(root) > 0;
        var pruned = PruneUnit(root) as ComboUnit; // root không bao giờ null khi còn lá (hadCandidates check)
        var flat = new List<ModelCandidate>();
        if (pruned is not null)
            Flatten(pruned, flat);

        if (flat.Count == 0)
        {
            // Combo rỗng/hết lá → 404 như item không tồn tại; có lá nhưng không lá OpenAI
            // nào → 503 (giữ semantics cũ: filter OpenAI xong rỗng = AnthropicNotSupported)
            return hadCandidates
                ? new SelectionFailure(model, ResolveFailure.AnthropicNotSupported)
                : new SelectionFailure(model, ResolveFailure.NotFound);
        }

        var deduped = flat
            .DistinctBy(c => (c.Provider.Id, c.Model.ModelId))
            .ToList();
        // RR sort flat field cho ổn định; TREE giữ Position order (selector sort at pick time)
        if (root.Mode == ComboMode.RoundRobin)
            deduped = deduped.OrderBy(c => c.Provider.Id).ThenBy(c => c.Model.ModelId).ToList();

        return new SelectionSuccess(deduped, root.Mode, root.Name, pruned);
    }

    private static int CountLeaves(SelectionNode node) => node switch
    {
        CandidateLeaf => 1,
        ComboUnit unit => unit.Children.Sum(CountLeaves),
        _ => 0,
    };

    /// <summary>Prune bottom-up: bỏ lá non-OpenAI, gỡ unit rỗng — unit không lá = không thể serve.</summary>
    private static SelectionNode? PruneUnit(SelectionNode node) => node switch
    {
        CandidateLeaf leaf when leaf.Candidate.Provider.Type == ProviderType.OpenAI => leaf,
        CandidateLeaf => null,
        ComboUnit unit =>
            BuildUnitOrEmpty(unit),
        _ => null,
    };

    private static ComboUnit? BuildUnitOrEmpty(ComboUnit unit)
    {
        var pruned = unit.Children
            .Select(PruneUnit)
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();
        // Prune xong rỗng → gỡ unit này khỏi cây (không tạo unit rỗng — selector sẽ park hoài)
        return pruned.Count == 0 ? null : unit with { Children = pruned };
    }

    private static void Flatten(SelectionNode node, List<ModelCandidate> into)
    {
        switch (node)
        {
            case CandidateLeaf leaf:
                into.Add(leaf.Candidate);
                break;
            case ComboUnit unit:
                foreach (var child in unit.Children)
                    Flatten(child, into);
                break;
        }
    }
}
