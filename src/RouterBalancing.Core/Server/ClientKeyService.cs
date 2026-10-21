using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;
using RouterBalancing.Core.Usage;

namespace RouterBalancing.Core.Server;

/// <inheritdoc cref="IClientKeyService"/>
public sealed class ClientKeyService : IClientKeyService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly IUsageDailyService _usage;

    public event Action? KeysChanged;

    /// <summary>
    /// Khởi tạo service với usage ledger dùng system clock — dành cho test/consumer
    /// không đi qua DI; DI dùng constructor có <see cref="IUsageDailyService"/>.
    /// </summary>
    public ClientKeyService(IDbContextFactory<RouterBalancingDbContext> db)
        : this(db, new UsageDailyService(db, TimeProvider.System))
    {
    }

    /// <summary>Khởi tạo service với dependency usage ledger được inject.</summary>
    public ClientKeyService(IDbContextFactory<RouterBalancingDbContext> db, IUsageDailyService usage)
    {
        _db = db;
        _usage = usage;
    }

    public async Task<IReadOnlyList<ClientKey>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var keys = await db.ClientKeys.AsNoTracking().OrderBy(k => k.Id).ToListAsync(ct);
        var totals = await _usage.GetTodayTotalsAsync(
            UsageSubjectKind.ClientKey, keys.Select(k => k.Id).ToList(), ct);
        foreach (var key in keys)
        {
            if (totals.TryGetValue(key.Id, out var usage))
            {
                key.RequestsUsed = usage.Requests;
                key.TokensUsed = usage.Tokens;
            }
        }
        return keys;
    }

    public async Task<(ClientKey Key, string Plaintext)> CreateAsync(ClientKeyDraft draft, CancellationToken ct = default)
    {
        Validate(draft);
        var plaintext = ClientKeyHasher.GeneratePlaintext();
        var now = DateTimeOffset.UtcNow;
        var entity = new ClientKey
        {
            Name = draft.Name.Trim(),
            KeyHash = ClientKeyHasher.Hash(plaintext),
            KeyMask = ClientKeyHasher.Mask(plaintext),
            Enabled = true,
            RatePerMinute = draft.RatePerMinute,
            TokensPerMinute = draft.TokensPerMinute,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using (var db = await _db.CreateDbContextAsync(ct))
        {
            db.ClientKeys.Add(entity);
            await db.SaveChangesAsync(ct);
        }
        KeysChanged?.Invoke();
        return (entity, plaintext);
    }

    public async Task UpdateAsync(long id, ClientKeyDraft draft, CancellationToken ct = default)
    {
        Validate(draft);
        await using (var db = await _db.CreateDbContextAsync(ct))
        {
            var key = await db.ClientKeys.FirstOrDefaultAsync(k => k.Id == id, ct)
                ?? throw new KeyNotFoundException($"Client key {id} không tồn tại.");
            key.Name = draft.Name.Trim();
            key.RatePerMinute = draft.RatePerMinute;
            key.TokensPerMinute = draft.TokensPerMinute;
            key.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        KeysChanged?.Invoke();
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using (var db = await _db.CreateDbContextAsync(ct))
        {
            var key = await db.ClientKeys.FirstOrDefaultAsync(k => k.Id == id, ct)
                ?? throw new KeyNotFoundException($"Client key {id} không tồn tại.");
            db.ClientKeys.Remove(key);
            await db.SaveChangesAsync(ct);
        }
        KeysChanged?.Invoke();
    }

    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
    {
        await using (var db = await _db.CreateDbContextAsync(ct))
        {
            var key = await db.ClientKeys.FirstOrDefaultAsync(k => k.Id == id, ct)
                ?? throw new KeyNotFoundException($"Client key {id} không tồn tại.");
            key.Enabled = enabled;
            key.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        KeysChanged?.Invoke();
    }

    public async Task RecordRequestAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var key = await db.ClientKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        // Key bị xóa giữa chừng sau khi auth qua — bỏ qua, không throw ra middleware
        if (key is null) return;
        key.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Ghi sổ sau khi key còn tồn tại; ledger tự bỏ qua nếu key bị xóa ngay sau đó.
        await _usage.RecordAsync(UsageSubjectKind.ClientKey, id, requests: 1, tokens: 0, ct);
    }

    public Task RecordTokensAsync(long id, int promptTokens, int completionTokens, CancellationToken ct = default)
        => _usage.RecordAsync(
            UsageSubjectKind.ClientKey, id, requests: 0, tokens: promptTokens + completionTokens, ct);

    // ArgumentException → UI bắt được và hiện validation message cạnh field (không cần custom exception)
    private static void Validate(ClientKeyDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
            throw new ArgumentException("Tên key là bắt buộc.", nameof(draft));
        if (draft.Name.Trim().Length > 100)
            throw new ArgumentException("Tên key tối đa 100 ký tự.", nameof(draft));
        if (draft.RatePerMinute is <= 0)
            throw new ArgumentException("RatePerMinute phải lớn hơn 0.", nameof(draft));
        if (draft.TokensPerMinute is <= 0)
            throw new ArgumentException("TokensPerMinute phải lớn hơn 0.", nameof(draft));
    }
}
