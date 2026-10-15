namespace RouterBalancing.Core.Providers;

/// <summary>
/// Quản lý process Go sidecar (opencode-proxy) — gateway TLS fingerprint
/// vượt gate 403 FreeTierError của OpenCode free tier.
/// </summary>
public interface IOpenCodeSidecarService : IDisposable
{
    /// <summary>Base URL của sidecar (http://127.0.0.1:{port}) — null nếu chưa start hoặc process đã chết.</summary>
    string? BaseUrl { get; }

    /// <summary>
    /// Start sidecar nếu chưa chạy; tự restart nếu process đã chết.
    /// Trả false nếu binary missing hoặc start thất bại — caller fallback path khác.
    /// </summary>
    Task<bool> EnsureStartedAsync(CancellationToken ct = default);
}
