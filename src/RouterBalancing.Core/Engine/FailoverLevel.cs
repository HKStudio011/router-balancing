namespace RouterBalancing.Core.Engine;

/// <summary>
/// Cấp failover của lỗi <see cref="DispatchOutcome.Fatal"/> — quyết định dispatcher
/// advance ở granularity nào (spec exhaustive-failover §3.1).
/// </summary>
public enum FailoverLevel
{
    /// <summary>Sai auth (401/403) — auth nằm ở account (spec §1.3 #6).</summary>
    Account,

    /// <summary>Provider chết / sai endpoint / lỗi mạng request-time.</summary>
    Provider,

    /// <summary>Model không tồn tại ở provider (404 + error.code=model_not_found).</summary>
    Model,
}
