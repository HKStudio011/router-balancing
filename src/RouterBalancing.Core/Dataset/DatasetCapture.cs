using RouterBalancing.Core.Engine;

namespace RouterBalancing.Core.Dataset;

/// <summary>
/// Một record dataset chờ writer — body gốc của client + completion dựng từ SSE state
/// đã có sẵn trong protocol (spec §7.6). Capture trên thread request chỉ giữ tham chiếu,
/// parse/serialize chạy trên thread writer để không nghẽn forward.
/// </summary>
/// <param name="RequestId">Id request — khớp record trong API Monitor ring.</param>
/// <param name="Endpoint">Đích endpoint (<see cref="ProxyEndpoint.Chat"/> hoặc <see cref="ProxyEndpoint.Responses"/>).</param>
/// <param name="FinishedAt">Thời điểm request kết thúc (tee xong), UTC — thành field <c>ts</c>.</param>
/// <param name="ClientBody">Body gốc của client, chưa chuẩn hoá — nguồn của <c>messages</c>/<c>tools</c>.</param>
/// <param name="CompletionJson">Completion JSON dựng từ SSE state; <see langword="null"/> khi stream cụt không terminal.</param>
/// <param name="CompletionComplete"><see langword="false"/> khi completion cụt (→ skip Truncated).</param>
/// <param name="Provider">Tên provider upstream thực nhận request.</param>
/// <param name="Account">Tên account được chọn.</param>
/// <param name="ModelUpstream">Model id thật gửi lên upstream.</param>
/// <param name="ModelRequested">Alias/model client gửi (từ API Monitor); <see langword="null"/> khi ring đã bay record.</param>
/// <param name="Mode">Mode combo (vd. <c>balancer</c>); <see langword="null"/> khi ring đã bay record.</param>
/// <param name="ClientKeyId">Id client key dùng cho request; <see langword="null"/> khi không có.</param>
/// <param name="PromptTokens">Usage prompt từ upstream; <see langword="null"/> khi upstream không trả usage.</param>
/// <param name="CompletionTokens">Usage completion từ upstream; <see langword="null"/> khi upstream không trả usage.</param>
/// <param name="Lane">Làn provenance (spec §7.4).</param>
/// <param name="ToolsRewritten"><see langword="true"/> khi sidecar đã shaping <c>tools</c> (header <c>X-Sidecar-Shaped</c> chứa "tools") — record là góc nhìn client, không phải thứ model thấy.</param>
public sealed record DatasetCapture(
    string RequestId,
    ProxyEndpoint Endpoint,
    DateTimeOffset FinishedAt,
    byte[] ClientBody,
    string? CompletionJson,
    bool CompletionComplete,
    string Provider,
    string Account,
    string ModelUpstream,
    string? ModelRequested,
    string? Mode,
    long? ClientKeyId,
    int? PromptTokens,
    int? CompletionTokens,
    DatasetLane Lane,
    bool ToolsRewritten);
