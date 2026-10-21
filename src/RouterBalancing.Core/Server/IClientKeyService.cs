using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Server;

/// <summary>
/// CRUD + ghi usage ngày cho API key inbound. <see cref="KeysChanged"/> phát sau mọi thay đổi
/// ảnh hưởng auth (create/update/delete/enable) để auth cache của proxy invalidate ngay (spec §4.1).
/// </summary>
public interface IClientKeyService
{
    event Action? KeysChanged;

    /// <summary>Lấy toàn bộ key (kèm <c>KeyMask</c> — không plaintext) cho UI, sắp theo id.</summary>
    Task<IReadOnlyList<ClientKey>> ListAsync(CancellationToken ct = default);

    /// <summary>Tạo key mới, trả plaintext 1 lần cho UI hiển thị — server không giữ lại.</summary>
    Task<(ClientKey Key, string Plaintext)> CreateAsync(ClientKeyDraft draft, CancellationToken ct = default);

    /// <summary>Cập nhật name + RPM/TPM của key; id không tồn tại → <see cref="KeyNotFoundException"/>.</summary>
    Task UpdateAsync(long id, ClientKeyDraft draft, CancellationToken ct = default);

    /// <summary>Xóa key theo id; id không tồn tại → <see cref="KeyNotFoundException"/>.</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Bật/tắt key theo id rồi phát <see cref="KeysChanged"/> để auth cache invalidate.</summary>
    Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);

    /// <summary>Ghi 1 request vào sổ UsageDaily + cập nhật LastUsedAt (gọi từ middleware sau khi auth qua).</summary>
    Task RecordRequestAsync(long id, CancellationToken ct = default);

    /// <summary>Ghi prompt+completion vào sổ UsageDaily (gọi từ usage sink).</summary>
    Task RecordTokensAsync(long id, int promptTokens, int completionTokens, CancellationToken ct = default);
}
