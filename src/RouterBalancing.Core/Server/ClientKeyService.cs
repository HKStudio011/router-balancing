using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Server;

/// <inheritdoc cref="IClientKeyService"/>
public sealed class ClientKeyService : IClientKeyService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;

    public event Action? KeysChanged;

    public ClientKeyService(IDbContextFactory<RouterBalancingDbContext> db) => _db = db;

    public async Task<IReadOnlyList<ClientKey>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.ClientKeys.AsNoTracking().OrderBy(k => k.Id).ToListAsync(ct);
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
        // Key bị xóa giữa chừng sau khi auth qua — bỏ qua counter, không throw ra middleware
        if (key is null) return;
        ResetDailyIfNeeded(key, DateTimeOffset.UtcNow);
        key.RequestsUsed++;
        key.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordTokensAsync(long id, int promptTokens, int completionTokens, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var key = await db.ClientKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null) return;
        ResetDailyIfNeeded(key, DateTimeOffset.UtcNow);
        key.TokensUsed += promptTokens + completionTokens;
        await db.SaveChangesAsync(ct);
    }

    // Reset lười tại thời điểm ghi thay vì job nền: counter daily không cần chính xác tuyệt đối
    // tại 00:00 UTC, chỉ cần không bao giờ cộng dồn qua ngày (spec §6.2).
    private static void ResetDailyIfNeeded(ClientKey key, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (key.UsageDate == today) return;
        key.UsageDate = today;
        key.RequestsUsed = 0;
        key.TokensUsed = 0;
    }

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
