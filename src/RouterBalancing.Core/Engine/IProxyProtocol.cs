using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>Đích endpoint của request — chọn <see cref="IProxyProtocol"/> tương ứng (spec §3.1).</summary>
public enum ProxyEndpoint
{
    /// <summary><c>/v1/chat/completions</c>.</summary>
    Chat = 0,

    /// <summary><c>/v1/responses</c>.</summary>
    Responses = 1,
}

/// <summary>
/// Seam protocol của pipeline queue-first: 3 điểm protocol-specific (validate /
/// upstream path + rewrite body / tee parse) tách khỏi <see cref="ProxyRequestHandler"/>
/// — handler và dispatcher dùng chung cho mọi endpoint (spec v1-responses §3.1).
/// </summary>
public interface IProxyProtocol
{
    /// <summary>Path upstream, nối vào BaseUrl đã canonicalize.</summary>
    string UpstreamPath { get; }

    /// <summary>Validate body thô từ client; lỗi → 400 OpenAI-style tại endpoint.</summary>
    ProxyValidationResult Validate(byte[] body);

    /// <summary>
    /// Đổi alias→model id thật + các rewrite bắt buộc trước khi forward.
    /// Trả kèm cờ <c>ExpectsUsage</c> (đã yêu cầu usage → upstream thiếu là bất thường, caller log Debug).
    /// </summary>
    /// <remarks>
    /// <c>providerType</c> cần cho rewrite kiểu chat (<c>stream_options.include_usage</c> chỉ
    /// upstream OpenAI-compatible hiểu) — spec §3.1 giữ 2 tham số là thiếu, plan đã refine.
    /// </remarks>
    (byte[] Body, bool ExpectsUsage) PrepareUpstreamBody(byte[] body, string realModelId,
        ProviderType providerType);

    /// <summary>Tee stream/JSON upstream vào dest, trích usage + first-token + body (cap 64KB).</summary>
    Task<UsageCapture.TeeResult> TeeAsync(HttpContent source, Stream dest, CancellationToken ct);
}

/// <summary>
/// Kết quả validate protocol-neutral cho runner — ghi 400 thẳng từ <c>ErrorMessage</c>/
/// <c>ErrorParam</c>, không switch qua enum riêng của protocol (spec §3.1).
/// </summary>
/// <param name="IsValid">Body hợp lệ — cho enqueue.</param>
/// <param name="ModelId">Model id trích được từ body; <see langword="null"/> khi failure.</param>
/// <param name="IsStream">Body có cờ stream bật — điều khiển pipe/keep-alive như chat.</param>
/// <param name="ErrorMessage">Message 400 OpenAI-style; <see langword="null"/> khi hợp lệ.</param>
/// <param name="ErrorParam">Trường gây lỗi (<c>param</c> trong error object); null khi không xác định.</param>
public readonly record struct ProxyValidationResult(
    bool IsValid, string? ModelId, bool IsStream, string? ErrorMessage, string? ErrorParam);
