using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Capabilities;

/// <inheritdoc cref="IModelCapabilityWriter"/>
public sealed class ModelCapabilityWriter : IModelCapabilityWriter
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ILogService _log;

    public ModelCapabilityWriter(IDbContextFactory<RouterBalancingDbContext> db, ILogService log)
    {
        _db = db;
        _log = log;
    }

    /// <inheritdoc />
    public async Task ApplyAsync(long modelId, CapabilitySource source, CapabilityPatch patch,
        bool sourceIsCompleteList, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        // Atomicity (fix review round 1): cả operation — upsert tracked lẫn mọi delete — chạy
        // trong MỘT transaction. Lỗi giữa đường (DbUpdateException, SQLITE_BUSY, key sai từ
        // vựng) → transaction bị dispose rollback, không để lại nửa phát biểu đã ghi/dã xóa.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var (kind, present) in patch.Capabilities)
        {
            // Luật (b): negative chỉ đến từ nguồn full-list (C-P) — đây là cách mọi
            // KnownAbsent trong DB đều an toàn cho reader (spec C-M).
            if (!present && !sourceIsCompleteList) continue;
            await UpsertCapabilityAsync(db, modelId, kind, knownAbsent: !present, source, ct);
        }
        foreach (var ((direction, modality), present) in patch.Modalities)
        {
            if (!present && !sourceIsCompleteList) continue;
            await UpsertModalityAsync(db, modelId, direction, modality, knownAbsent: !present, source, ct);
        }
        foreach (var level in NormalizeLevels(patch.ReasoningEfforts))
            await UpsertEffortAsync(db, modelId, level, source, ct);
        await SaveChangesAsync(db, nameof(ApplyAsync), modelId, ct);
        await tx.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task ApplyUserEditAsync(long modelId, CapabilityPatch patch, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        // Atomicity (fix review round 1): ClearKeys + các upsert phải là MỘT transaction —
        // nếu phần ghi fail thì các clear của user cũng phải bị rollback (không nửa edit).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Luật (c) xử lý ClearKeys TRƯỚC khi ghi các key khác (brief Step 3): ExecuteDelete
        // chạy thẳng trên DB nên không va chạm PK với Add cùng key trong một SaveChanges
        // (EF không đảm bảo thứ tự delete trước insert cho cùng hàng).
        foreach (var (kind, key) in patch.ClearKeys)
            await DeleteByClearKeyAsync(db, modelId, kind, key, ct);

        // Ghi user: luôn thắng mọi nguồn (C-N). Upsert tại chỗ thay vì delete+insert —
        // PK ghép đảm bảo tối đa một dòng mỗi key, mutate cho kết quả quan sát được
        // y hệt "xóa dòng cũ bất kể nguồn rồi ghi dòng Source=User" của luật (c).
        foreach (var (kind, present) in patch.Capabilities)
            await UpsertCapabilityAsync(db, modelId, kind, knownAbsent: !present, CapabilitySource.User, ct);
        foreach (var ((direction, modality), present) in patch.Modalities)
            await UpsertModalityAsync(db, modelId, direction, modality, knownAbsent: !present, CapabilitySource.User, ct);
        foreach (var level in NormalizeLevels(patch.ReasoningEfforts))
            await UpsertEffortAsync(db, modelId, level, CapabilitySource.User, ct);
        await SaveChangesAsync(db, nameof(ApplyUserEditAsync), modelId, ct);
        await tx.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task ClearSourceAsync(long modelId, CapabilitySource source, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        // Ba delete liên bảng chỉ có nghĩa nguyên tử khi cùng transaction (fix review round 1).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ModelCapabilities.Where(x => x.ModelId == modelId && x.Source == source).ExecuteDeleteAsync(ct);
        await db.ModelModalities.Where(x => x.ModelId == modelId && x.Source == source).ExecuteDeleteAsync(ct);
        await db.ModelReasoningEfforts.Where(x => x.ModelId == modelId && x.Source == source).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task ClearAllAsync(long modelId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ModelCapabilities.Where(x => x.ModelId == modelId).ExecuteDeleteAsync(ct);
        await db.ModelModalities.Where(x => x.ModelId == modelId).ExecuteDeleteAsync(ct);
        await db.ModelReasoningEfforts.Where(x => x.ModelId == modelId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    // ---------- upsert theo bảng (luật a + d) ----------

    private static async Task UpsertCapabilityAsync(RouterBalancingDbContext db, long modelId,
        CapabilityKind kind, bool knownAbsent, CapabilitySource source, CancellationToken ct)
    {
        var existing = await db.ModelCapabilities
            .FirstOrDefaultAsync(x => x.ModelId == modelId && x.Capability == kind, ct);
        if (!MayWrite(existing, source)) return;
        if (existing is null)
        {
            db.ModelCapabilities.Add(new ModelCapability
            {
                ModelId = modelId,
                Capability = kind,
                KnownAbsent = knownAbsent,
                Source = source,
                UpdatedAt = DateTimeOffset.UtcNow, // luật (d) — ghi tường minh, không dựa default của entity
            });
        }
        else
        {
            existing.KnownAbsent = knownAbsent;
            existing.Source = source;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static async Task UpsertModalityAsync(RouterBalancingDbContext db, long modelId,
        ModalityDirection direction, ModalityKind modality, bool knownAbsent, CapabilitySource source, CancellationToken ct)
    {
        var existing = await db.ModelModalities
            .FirstOrDefaultAsync(x => x.ModelId == modelId && x.Direction == direction && x.Modality == modality, ct);
        if (!MayWrite(existing, source)) return;
        if (existing is null)
        {
            db.ModelModalities.Add(new ModelModality
            {
                ModelId = modelId,
                Direction = direction,
                Modality = modality,
                KnownAbsent = knownAbsent,
                Source = source,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.KnownAbsent = knownAbsent;
            existing.Source = source;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static async Task UpsertEffortAsync(RouterBalancingDbContext db, long modelId,
        string level, CapabilitySource source, CancellationToken ct)
    {
        var existing = await db.ModelReasoningEfforts
            .FirstOrDefaultAsync(x => x.ModelId == modelId && x.Level == level, ct);
        if (!MayWrite(existing, source)) return;
        if (existing is null)
        {
            db.ModelReasoningEfforts.Add(new ModelReasoningEffort
            {
                ModelId = modelId,
                Level = level,
                Source = source,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Source = source;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Luật (a): User luôn được ghi; nguồn máy móc chỉ ghi khi dòng chưa tồn tại
    /// hoặc đang mang đúng nguồn đó (C-E/C-N).
    /// </summary>
    private static bool MayWrite(ModelCapability? existing, CapabilitySource source) =>
        existing is null || source == CapabilitySource.User || existing.Source == source;

    /// <inheritdoc cref="MayWrite(ModelCapability?, CapabilitySource)"/>
    private static bool MayWrite(ModelModality? existing, CapabilitySource source) =>
        existing is null || source == CapabilitySource.User || existing.Source == source;

    /// <inheritdoc cref="MayWrite(ModelCapability?, CapabilitySource)"/>
    private static bool MayWrite(ModelReasoningEffort? existing, CapabilitySource source) =>
        existing is null || source == CapabilitySource.User || existing.Source == source;

    // ---------- chuẩn hóa + parsing ----------

    /// <summary>
    /// Chuẩn hoá level theo ruling R-T1-4: PK Level là TEXT và SQLite phân biệt
    /// hoa/thường — "High" và "high" sẽ thành hai dòng nếu không chuẩn hoá.
    /// Level rỗng sau trim là giá trị vô nghĩa, bỏ qua.
    /// </summary>
    private static IEnumerable<string> NormalizeLevels(IReadOnlySet<string> levels) =>
        levels.Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0).Distinct();

    private async Task DeleteByClearKeyAsync(RouterBalancingDbContext db, long modelId,
        string kind, string key, CancellationToken ct)
    {
        switch (kind)
        {
            case CapabilityPatch.CapabilityKeyKind:
            {
                var capability = CapabilityNames.FromDb<CapabilityKind>(key);
                await db.ModelCapabilities
                    .Where(x => x.ModelId == modelId && x.Capability == capability)
                    .ExecuteDeleteAsync(ct);
                break;
            }
            case CapabilityPatch.ModalityKeyKind:
            {
                var parts = key.Split('/');
                if (parts.Length != 2)
                    throw new FormatException($"Malformed modality clear-key: '{key}' (expected 'direction/modality').");
                var direction = CapabilityNames.FromDb<ModalityDirection>(parts[0]);
                var modality = CapabilityNames.FromDb<ModalityKind>(parts[1]);
                await db.ModelModalities
                    .Where(x => x.ModelId == modelId && x.Direction == direction && x.Modality == modality)
                    .ExecuteDeleteAsync(ct);
                break;
            }
            case CapabilityPatch.EffortKeyKind:
            {
                // Clear cũng chuẩn hoá level như chiều ghi (R-T1-4) — user gõ "High"
                // phải xóa được dòng "high".
                var level = key.Trim().ToLowerInvariant();
                await db.ModelReasoningEfforts
                    .Where(x => x.ModelId == modelId && x.Level == level)
                    .ExecuteDeleteAsync(ct);
                break;
            }
            default:
                // Token ClearKeys là hợp đồng nội bộ — key lạ là lỗi chương trình,
                // nổ to chứ không âm thầm bỏ qua phát biểu xóa của user.
                throw new ArgumentException($"Unknown capability clear-key kind: '{kind}'.", nameof(kind));
        }
    }

    // ---------- save ----------

    private async Task SaveChangesAsync(RouterBalancingDbContext db, string operation, long modelId, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Ném nguyên trạng cho caller, nhưng log thêm ngữ cảnh model/operation mà
            // caller (importer/UI) thường không có sẵn khi stack đã unwind.
            _log.Error($"Capability write failed ({operation}, model {modelId}).", ex);
            throw;
        }
    }
}
