using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Usage;

/// <summary>
/// Đọc/ghi sổ usage ngày UTC. Ghi là atomic upsert có điều kiện chủ thể còn tồn tại;
/// đọc là một query theo tập id để service UI không phải N+1.
/// </summary>
public interface IUsageDailyService
{
    /// <summary>
    /// Cộng dồn request/token vào dòng hôm nay của chủ thể. Nếu chủ thể đã bị xóa
    /// giữa chừng thì bỏ qua (không tạo dòng mồ côi); đây là telemetry nên không ném
    /// lỗi vì chủ thể biến mất.
    /// </summary>
    /// <param name="kind">Loại chủ thể.</param>
    /// <param name="subjectId">Id trong bảng ClientKeys/ProviderAccounts.</param>
    /// <param name="requests">Số request cộng thêm (0 nếu chỉ ghi token).</param>
    /// <param name="tokens">Số token cộng thêm (0 nếu chỉ ghi request).</param>
    /// <param name="ct">Token hủy request.</param>
    Task RecordAsync(UsageSubjectKind kind, long subjectId, long requests, long tokens,
        CancellationToken ct = default);

    /// <summary>
    /// Tổng usage hôm nay (UTC) cho các chủ thể được yêu cầu. Id không có dòng usage
    /// sẽ vắng mặt trong dictionary — caller coi như 0.
    /// </summary>
    /// <param name="kind">Loại chủ thể.</param>
    /// <param name="subjectIds">Tập id cần đọc; rỗng thì trả dictionary rỗng, không query.</param>
    /// <param name="ct">Token hủy request.</param>
    Task<IReadOnlyDictionary<long, UsageTotals>> GetTodayTotalsAsync(
        UsageSubjectKind kind, IReadOnlyCollection<long> subjectIds, CancellationToken ct = default);
}
