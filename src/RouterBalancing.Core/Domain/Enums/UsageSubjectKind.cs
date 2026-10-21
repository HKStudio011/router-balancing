namespace RouterBalancing.Core.Domain;

/// <summary>
/// Loại chủ thể sở hữu usage trong <see cref="UsageDaily"/> — client key inbound
/// hoặc provider account outbound. Một bảng chung để lịch sử usage có cùng shape,
/// không phụ thuộc bảng cấu hình của từng loại.
/// </summary>
public enum UsageSubjectKind
{
    /// <summary>API key inbound của client (bảng <c>ClientKeys</c>).</summary>
    ClientKey = 0,

    /// <summary>API key outbound của provider (bảng <c>ProviderAccounts</c>).</summary>
    ProviderAccount = 1,
}

/// <summary>
/// Storage contract cho <see cref="UsageDaily.SubjectKind"/>: lưu TEXT snake_case
/// thay vì số enum để migration/DB bên ngoài đọc được mà không cần map lại.
/// </summary>
public static class UsageSubjectKinds
{
    /// <summary>Giá trị lưu cho <see cref="UsageSubjectKind.ClientKey"/>.</summary>
    public const string ClientKey = "client_key";

    /// <summary>Giá trị lưu cho <see cref="UsageSubjectKind.ProviderAccount"/>.</summary>
    public const string ProviderAccount = "provider_account";

    /// <summary>Chuyển enum sang token lưu DB.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Khi <paramref name="kind"/> không hợp lệ.</exception>
    public static string ToDb(UsageSubjectKind kind) => kind switch
    {
        UsageSubjectKind.ClientKey => ClientKey,
        UsageSubjectKind.ProviderAccount => ProviderAccount,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown usage subject kind."),
    };

    /// <summary>Đọc token từ DB về enum.</summary>
    /// <exception cref="FormatException">Khi token không thuộc contract hiện tại.</exception>
    public static UsageSubjectKind FromDb(string value) => value switch
    {
        ClientKey => UsageSubjectKind.ClientKey,
        ProviderAccount => UsageSubjectKind.ProviderAccount,
        _ => throw new FormatException($"Unknown usage subject kind '{value}'."),
    };
}
