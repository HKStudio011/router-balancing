namespace RouterBalancing.Core.Engine;

/// <summary>1 request đang được phục vụ — data source cho snapshot và cancel-409 (spec §2.1).</summary>
/// <param name="RequestId">Id request (8 ký tự).</param>
/// <param name="ProviderId">Provider đang giữ request.</param>
/// <param name="ProviderName">Tên provider (snapshot không cần join DB).</param>
/// <param name="Model">ModelId đang serve.</param>
/// <param name="Priority">Priority tại thời điểm enqueue.</param>
/// <param name="EnqueuedAt">Thời điểm vào queue.</param>
/// <param name="StartedAt">Thời điểm bắt đầu serve (lúc TryEnter thành công).</param>
/// <param name="AccountId">TK giữ slot — 0 = sentinel khi provider không có TK enabled nào (D-B3, V1).</param>
/// <param name="AccountName">Tên TK; chuỗi rỗng ở sentinel.</param>
public sealed record ExecutionEntry(
    string RequestId,
    long ProviderId,
    string ProviderName,
    string Model,
    RequestPriority Priority,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset StartedAt,
    long AccountId,
    string AccountName);
