namespace RouterBalancing.Core.Domain;

/// <summary>
/// Sổ usage theo ngày UTC, thay các counter <c>RequestsUsed/TokensUsed/UsageDate</c>
/// nằm rải trong bảng cấu hình (backlog C3). Mỗi dòng là tổng của một chủ thể trong
/// một ngày; sang ngày mới chỉ cần ghi dòng mới, không reset dữ liệu cũ.
/// <para>
/// Không có FK tới ClientKeys/ProviderAccounts vì SubjectId là quan hệ đa hình:
/// xóa key/account vẫn giữ lịch sử usage để thống kê. Vì vậy SubjectId có thể trỏ
/// tới chủ thể đã bị xóa — đây là chủ ý, không phải dữ liệu hỏng.
/// </para>
/// </summary>
public class UsageDaily
{
    /// <summary>Loại chủ thể của <see cref="SubjectId"/>.</summary>
    public UsageSubjectKind SubjectKind { get; set; }

    /// <summary>Khóa của chủ thể trong bảng tương ứng; không FK (xem ghi chú class).</summary>
    public long SubjectId { get; set; }

    /// <summary>Ngày UTC của usage — khóa ghép cùng <see cref="SubjectKind"/> + <see cref="SubjectId"/>.</summary>
    public DateOnly Date { get; set; }

    /// <summary>Số request đã ghi nhận trong ngày.</summary>
    public long Requests { get; set; }

    /// <summary>Số token (prompt + completion) đã ghi nhận trong ngày.</summary>
    public long Tokens { get; set; }
}
