using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Engine;

/// <inheritdoc/>
public sealed class ModelResolver(IDbContextFactory<RouterBalancingDbContext> db) : IModelResolver
{
    /// <inheritdoc/>
    public async Task<ModelResolveResult> ResolveAsync(string modelId, CancellationToken ct)
    {
        using var context = db.CreateDbContext();
        var model = await context.Models.AsNoTracking()
            .Include(m => m.Provider!)
            .ThenInclude(p => p.Accounts)
            .Where(m => m.ModelId == modelId && m.Enabled && m.Provider != null && m.Provider.Enabled)
            .OrderBy(m => m.Id)
            .FirstOrDefaultAsync(ct);

        // Filter trong Where đã lo provider enabled; null (dangling FK) → NotFound
        if (model?.Provider is not { } provider)
            return new ModelResolveFailure(modelId, ResolveFailure.NotFound);

        // Check Anthropic SAU khi qua được filter enabled (spec §2.3): tắt → 404, bật → 503
        if (provider.Type == ProviderType.Anthropic)
            return new ModelResolveFailure(modelId, ResolveFailure.AnthropicNotSupported);

        return new ModelResolveSuccess(provider, model);
    }
}
