namespace RouterBalancing.Core.Server;

/// <summary>Rate limit RPM/TPM theo key trên window 60s in-memory (spec §6).</summary>
public interface IClientKeyRateLimiter
{
    /// <summary>
    /// Tiêu 1 request vào window của key. Trả <c>(false, retryAfterSec)</c> khi vượt RPM
    /// hoặc token window đã chạm TPM; caller trả 429 + Retry-After.
    /// </summary>
    (bool Allowed, int RetryAfterSec) TryEnter(long keyId, int? ratePerMinute, int? tokensPerMinute);

    /// <summary>Cộng token usage (prompt+completion) vào window hiện tại — chỉ để chặn request sau (spec §6).</summary>
    void AddTokens(long keyId, int tokens);
}
