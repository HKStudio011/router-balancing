using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Combos;

/// <inheritdoc/>
public sealed class ComboService : IComboService
{
    private const int MaxNameLength = 200;

    private readonly IDbContextFactory<RouterBalancingDbContext> _db;

    /// <inheritdoc/>
    public ComboService(IDbContextFactory<RouterBalancingDbContext> db)
    {
        _db = db;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Combo>> ListAsync(CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        return await db.Combos.Include(c => c.Items).OrderBy(c => c.Name).ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<Combo> CreateAsync(ComboDraft draft, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var name = NormalizeName(draft.Name);
        await ValidateAsync(db, name, draft.Items, selfId: null, ct);

        var combo = new Combo { Name = name, Mode = draft.Mode };
        for (var i = 0; i < draft.Items.Count; i++)
        {
            combo.Items.Add(ToItem(draft.Items[i], i));
        }

        db.Combos.Add(combo);
        await db.SaveChangesAsync(ct);
        return combo;
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(long id, ComboDraft draft, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var combo = await db.Combos.Include(c => c.Items)
                .FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new KeyNotFoundException($"Combo {id} not found.");
        var name = NormalizeName(draft.Name);
        await ValidateAsync(db, name, draft.Items, selfId: id, ct);

        combo.Name = name;
        combo.Mode = draft.Mode;
        // Thay thế toàn bộ items: Position tính lại theo thứ tự draft (spec §3)
        db.ComboItems.RemoveRange(combo.Items);
        combo.Items.Clear();
        for (var i = 0; i < draft.Items.Count; i++)
        {
            combo.Items.Add(ToItem(draft.Items[i], i));
        }

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var combo = await db.Combos.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new KeyNotFoundException($"Combo {id} not found.");
        var referencing = await db.ComboItems.CountAsync(ci => ci.TargetComboId == id, ct);
        if (referencing > 0)
        {
            // FK Restrict sẽ nổ nếu bỏ qua — chặn sớm với thông điệp rõ ràng
            throw new InvalidOperationException($"Combo {id} is referenced by {referencing} item(s).");
        }

        db.Combos.Remove(combo);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetReferencingComboNamesAsync(long id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        return await db.Combos
            .Where(c => c.Items.Any(ci => ci.TargetComboId == id))
            .OrderBy(c => c.Name)
            .Select(c => c.Name)
            .ToListAsync(ct);
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        // Rỗng → ArgumentException (nhất quán ThrowIfNullOrWhiteSpace của repo); UI CanSave chặn trước
        return trimmed.Length == 0
            ? throw new ArgumentException("Combo name is required.", nameof(name))
            : trimmed;
    }

    private static ComboItem ToItem(ComboItemDraft draft, int position) => new()
    {
        TargetModelId = draft.TargetModelId,
        TargetComboId = draft.TargetComboId,
        Position = position,
    };

    private static async Task ValidateAsync(RouterBalancingDbContext db, string name,
        IReadOnlyList<ComboItemDraft> items, long? selfId, CancellationToken ct)
    {
        if (name.Length > MaxNameLength)
        {
            throw new ComboValidationException(ComboValidationError.NameTooLong,
                $"Name must be {MaxNameLength} characters or fewer.");
        }

        if (items.Count == 0)
        {
            throw new ComboValidationException(ComboValidationError.EmptyItems,
                "Combo needs at least one item.");
        }

        if (await db.Combos.AnyAsync(c => c.Name == name && c.Id != selfId, ct))
        {
            throw new ComboValidationException(ComboValidationError.DuplicateName,
                $"A combo named '{name}' already exists.");
        }

        foreach (var item in items)
        {
            var hasModel = item.TargetModelId is not null;
            var hasCombo = item.TargetComboId is not null;
            if (hasModel == hasCombo) // cả 2 null hoặc cả 2 có → sai XOR (spec §3)
            {
                throw new ComboValidationException(ComboValidationError.InvalidItemTarget,
                    "Each item must target exactly one model or combo.");
            }

            if (hasModel && !await db.Models.AnyAsync(m => m.Id == item.TargetModelId, ct))
            {
                throw new ComboValidationException(ComboValidationError.TargetNotFound,
                    $"Model {item.TargetModelId} not found.");
            }

            if (hasCombo && !await db.Combos.AnyAsync(c => c.Id == item.TargetComboId, ct))
            {
                throw new ComboValidationException(ComboValidationError.TargetNotFound,
                    $"Combo {item.TargetComboId} not found.");
            }
        }

        if (selfId is not null)
        {
            await ValidateNoCycleAsync(db, selfId.Value, items, ct);
        }
    }

    private static async Task ValidateNoCycleAsync(RouterBalancingDbContext db, long selfId,
        IReadOnlyList<ComboItemDraft> items, CancellationToken ct)
    {
        // Đồ thị: X → Y = combo X chứa combo Y (từ TargetComboId). Thêm self → C tạo
        // cycle đúng khi đi từ C theo cạnh "chứa" chạm self — tức C là self hoặc tổ tiên của self.
        // CreateAsync truyền selfId null vì id mới chưa combo nào trỏ tới (spec §3).
        var edges = await db.ComboItems
            .Where(ci => ci.TargetComboId != null)
            .Select(ci => new { ci.ComboId, Target = ci.TargetComboId!.Value })
            .ToListAsync(ct);
        var children = edges
            .GroupBy(e => e.ComboId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Target).ToList());

        foreach (var item in items)
        {
            if (item.TargetComboId is not { } start)
            {
                continue;
            }

            var visited = new HashSet<long>();
            var stack = new Stack<long>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == selfId)
                {
                    throw new ComboValidationException(ComboValidationError.CycleDetected,
                        $"Combo {selfId} would contain itself through combo {start}.");
                }

                if (!visited.Add(current))
                {
                    continue;
                }

                if (children.TryGetValue(current, out var next))
                {
                    foreach (var child in next)
                    {
                        stack.Push(child);
                    }
                }
            }
        }
    }
}
