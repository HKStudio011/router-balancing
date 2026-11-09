namespace RouterBalancing.Core.Dataset;

/// <summary>
/// Làn provenance của một record dataset — để offline lọc theo cách request được xử lý
/// mà không cần biết tool name (spec §7.4).
/// </summary>
public enum DatasetLane
{
    /// <summary>Request thường — không qua fingerprint OpenCode.</summary>
    Plain = 0,

    /// <summary>Fingerprint OpenCode trên account free (key rỗng).</summary>
    FreeFingerprint = 1,

    /// <summary>Fingerprint OpenCode trên account dùng key thật.</summary>
    KeyedFingerprint = 2,
}
