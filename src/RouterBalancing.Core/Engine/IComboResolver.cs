using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>Candidate đã resolve: provider + model tương ứng (T4 chọn, T6 serve).</summary>
public sealed record ModelCandidate(Provider Provider, Model Model);

/// <summary>Nút trong cây selection — lá = 1 candidate; đơn vị combo = mode + danh sách con.</summary>
public abstract record SelectionNode;

/// <summary>Lá của cây selection — 1 provider/model pair.</summary>
/// <param name="Candidate">Candidate tương ứng.</param>
public sealed record CandidateLeaf(ModelCandidate Candidate) : SelectionNode;

/// <summary>
/// Đơn vị combo trong cây — chọn trong <see cref="Children"/> theo <see cref="Mode">
/// của CHÍNH combo này (composition: tôn trọng mode từng cấp, không flatten).
/// </summary>
/// <param name="Name">Tên combo (điều hướng trace breadcrumb).</param>
/// <param name="Mode">Chế độ của combo này — áp dụng đệ quy cho subtree.</param>
/// <param name="Children">Thứ tự theo Position của combo; item con = <see cref="ComboUnit"/>, item model = <see cref="CandidateLeaf"/>.</param>
public sealed record ComboUnit(string Name, ComboMode Mode, IReadOnlyList<SelectionNode> Children)
    : SelectionNode;

/// <summary>Kết quả resolve theo model string (spec §3.2).</summary>
public abstract record SelectionResult;

/// <summary>Resolve thành công — danh sách candidate + mode để chọn (spec §3.3).</summary>
/// <param name="Candidates">Đã dedup + filter OpenAI; RR sort (ProviderId, ModelId), Fallback giữ thứ tự Position.</param>
/// <param name="Mode">RoundRobin với model id; combo mode khi resolve qua combo.</param>
/// <param name="ComboName">Tên combo đã resolve; <see langword="null"/> khi match model id.</param>
/// <param name="Root">Cây selection khi resolve qua combo (mode từng cấp được giữ);
/// <see langword="null"/> = flat path (model id) — selector dùng <paramref name="Candidates"/> như cũ.</param>
public sealed record SelectionSuccess(
    IReadOnlyList<ModelCandidate> Candidates, ComboMode Mode, string? ComboName = null,
    SelectionNode? Root = null)
    : SelectionResult;

/// <summary>Resolve thất bại — endpoint/dispatcher ghi lỗi theo <paramref name="Reason"/>.</summary>
/// <param name="ModelId">Chuỗi model client gửi (message 404 dùng chuỗi này).</param>
/// <param name="Reason">NotFound → 404; AnthropicNotSupported → 503.</param>
public sealed record SelectionFailure(string ModelId, ResolveFailure Reason) : SelectionResult;

/// <summary>Resolve model string → candidate list (model id trước, combo name sau — spec §3.2).</summary>
public interface IComboResolver
{
    /// <summary>Resolve bất đồng bộ — hủy theo <paramref name="ct"/> khi request bị client ngắt.</summary>
    /// <param name="model">Chuỗi <c>model</c> client gửi (model id hoặc tên combo).</param>
    /// <param name="ct">Token hủy theo request.</param>
    Task<SelectionResult> ResolveAsync(string model, CancellationToken ct);
}
