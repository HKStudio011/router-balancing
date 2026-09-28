namespace RouterBalancing.Core.Engine;

/// <summary>Mức ưu tiên request trong queue — luật 1-Highest xem <see cref="RequestQueue"/>.</summary>
public enum RequestPriority
{
    /// <summary>Mức mặc định — header <c>X-Priority</c> thiếu/không hợp lệ cũng trả về mức này, xếp sau High/Highest khi chọn head.</summary>
    Normal = 0,

    /// <summary>Mức cao — header <c>X-Priority: high</c>, được chọn trước Normal nhưng nhường Highest.</summary>
    High = 1,

    /// <summary>Mức cao nhất — header <c>X-Priority: max</c>; luật 1-Highest: request mới sẽ hạ cấp các Highest khác xuống High.</summary>
    Highest = 2,
}
