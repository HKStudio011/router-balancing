namespace RouterBalancing.Core.Engine;

/// <summary>
/// Trạng thái retry của MỘT request: tập (providerId, modelId) đã thử + thất bại
/// gần nhất (spec manual-retry §2.2). Sống trên <see cref="ProxyRequest"/> nên giữ nguyên qua park
/// và capacity-corner re-enqueue — dispatch lại chỉ xét candidate chưa thử, không có
/// vòng lặp nóng. Không lock: 1 logical owner (dispatcher serve task); happens-before
/// qua queue lock (ghi trước khi re-Enqueue, đọc sau Peek/Take).
/// <para>Trạng thái cấp failover (spec exhaustive-failover §2.3): lỗi cấp
/// <see cref="FailoverLevel.Account"/> → chỉ <see cref="MarkAccountTried"/> (pair đánh dấu
/// sau ở <c>NoAccountLeft</c>); cấp <see cref="FailoverLevel.Provider"/> →
/// <see cref="MarkProviderFailed"/> và <see cref="MarkTried(long, string)"/> (đảm bảo
/// <see cref="HasTried"/> = <see langword="true"/> → exhaustion thay vì 503 walk-rỗng);
/// cấp <see cref="FailoverLevel.Model"/> → <see cref="MarkTried(long, string)"/>.</para>
/// </summary>
public sealed class RetryState
{
    private readonly HashSet<(long ProviderId, string ModelId)> _tried = [];
    private readonly HashSet<(long ProviderId, long AccountId)> _triedAccounts = [];
    private readonly HashSet<long> _failedProviders = [];
    private readonly List<AttemptRecord> _trail = [];

    /// <summary>Thất bại gần nhất của 1 attempt — exhaustion convert thành Passthrough/Error(502).</summary>
    /// <param name="Status"><see langword="null"/> = lỗi mạng (không có HTTP response nào).</param>
    /// <param name="ContentType">Content-Type upstream trả (null khi lỗi mạng).</param>
    /// <param name="Body">Body đã buffer — response lỗi nhỏ, chưa commit (rỗng khi lỗi mạng).</param>
    /// <param name="RetryAfter"><c>Retry-After</c> đã parse — format lại header khi passthrough.</param>
    public sealed record Failure(int? Status, string? ContentType, byte[] Body, TimeSpan? RetryAfter);

    /// <summary>
    /// Một attempt đã gọi — lịch sử cho <c>Details</c> JSON của log exhaustion (Task 6).
    /// Giữ tối thiểu: chỉ provider/model/account/status — không body request, không API key.
    /// </summary>
    /// <param name="Provider">Tên provider đã gửi request.</param>
    /// <param name="Model">Model đã gọi.</param>
    /// <param name="Account">Tên tài khoản đã dùng (không chứa secret).</param>
    /// <param name="Status"><see langword="null"/> = lỗi mạng (không có HTTP response nào).</param>
    public sealed record AttemptRecord(string Provider, string Model, string Account, int? Status);

    /// <summary>
    /// Thất bại gần nhất — nhận từ cả <see cref="DispatchOutcome.Retryable"/> lẫn
    /// <see cref="DispatchOutcome.Fatal"/>; attempt cuối quyết định exhaustion (spec §2.2).
    /// </summary>
    public Failure? LastFailure { get; set; }

    /// <summary>Đã thử ít nhất 1 candidate chưa (phân biệt 503 walk-rỗng vs exhaustion).</summary>
    public bool HasTried => _tried.Count > 0;

    /// <summary>Số lần thử — cùng 1 model trên 2 provider tính 2 (2 cặp khác nhau).</summary>
    public int TriedCount => _tried.Count;

    /// <summary>Số lần gọi <c>ForwardAsync</c> — k trong log k/N của exhaustion (spec §2.3).</summary>
    public int Attempts { get; private set; }

    /// <summary>Lịch sử attempt theo thứ tự gọi — Task 6 serialize thành <c>Details</c> của log exhaustion.</summary>
    public IReadOnlyList<AttemptRecord> Trail => _trail;

    /// <summary>Ghi nhận đã thử 1 candidate.</summary>
    /// <param name="providerId">Id provider của candidate.</param>
    /// <param name="modelId">Model id của candidate.</param>
    public void MarkTried(long providerId, string modelId) => _tried.Add((providerId, modelId));

    /// <summary>Cặp (provider, model) này đã thử chưa — filter candidate khi walk/dispatch lại.</summary>
    /// <param name="providerId">Id provider của candidate.</param>
    /// <param name="modelId">Model id của candidate.</param>
    public bool IsTried(long providerId, string modelId) => _tried.Contains((providerId, modelId));

    /// <summary>Tăng bộ đếm attempt và append 1 bản ghi vào <see cref="Trail"/>.</summary>
    /// <param name="provider">Tên provider đã gửi request.</param>
    /// <param name="model">Model đã gọi.</param>
    /// <param name="account">Tên tài khoản đã dùng — không chứa secret.</param>
    /// <param name="status">HTTP status; <see langword="null"/> = lỗi mạng (không có HTTP response).</param>
    public void RecordAttempt(string provider, string model, string account, int? status)
    {
        Attempts++;
        _trail.Add(new AttemptRecord(provider, model, account, status));
    }

    /// <summary>Ghi nhận TK fail cấp Account — scope theo provider (TK cùng id ở provider khác là 2 TK độc lập).</summary>
    /// <param name="providerId">Id provider chứa TK.</param>
    /// <param name="accountId">Id TK đã thử.</param>
    public void MarkAccountTried(long providerId, long accountId) => _triedAccounts.Add((providerId, accountId));

    /// <summary>TK này của provider này đã thử chưa — exclude khi advance account kế (<c>TryEnter</c>).</summary>
    /// <param name="providerId">Id provider chứa TK.</param>
    /// <param name="accountId">Id TK cần xét.</param>
    public bool IsAccountTried(long providerId, long accountId) => _triedAccounts.Contains((providerId, accountId));

    /// <summary>Ghi nhận provider fail cấp Provider — filter loại mọi candidate của provider này.</summary>
    /// <param name="providerId">Id provider chết (lỗi mạng / 404 khác).</param>
    public void MarkProviderFailed(long providerId) => _failedProviders.Add(providerId);

    /// <summary>Provider này đã fail cấp Provider chưa — skip toàn bộ candidate cùng provider.</summary>
    /// <param name="providerId">Id provider cần xét.</param>
    public bool IsProviderFailed(long providerId) => _failedProviders.Contains(providerId);
}
