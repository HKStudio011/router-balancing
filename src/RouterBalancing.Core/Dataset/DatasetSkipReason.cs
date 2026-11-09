namespace RouterBalancing.Core.Dataset;

/// <summary>
/// Lý do một record bị loại (chỉ đếm, không ghi) — "record nửa vời độc hơn mất mẫu"
/// nên bản hỏng không được vào dataset train (spec §7.6).
/// </summary>
public enum DatasetSkipReason
{
    /// <summary>Body hoặc completion vượt cap 1MB (<c>MaxFieldBytes</c>, Task 3).</summary>
    TooLarge = 0,

    /// <summary>Completion cụt giữa stream — upstream đóng không terminal / response.failed.</summary>
    Truncated = 1,

    /// <summary>Output/completion rỗng hoặc thiếu (<c>CompletionJson</c> null).</summary>
    Empty = 2,

    /// <summary>Upstream trả 2xx nhưng completion là lời từ chối (heuristic chuỗi, spec §7.8).</summary>
    Refusal = 3,
}
