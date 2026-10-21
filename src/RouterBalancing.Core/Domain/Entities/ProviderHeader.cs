namespace RouterBalancing.Core.Domain;

/// <summary>
/// Header tùy chỉnh gửi kèm mọi request tới provider — thay cột JSON
/// <c>Providers.ExtraHeaders</c>. PK ghép <c>(ProviderId, Name)</c> dùng collation
/// <c>NOCASE</c> vì HTTP header name không phân biệt hoa thường.
/// </summary>
public class ProviderHeader
{
    public long ProviderId { get; set; }

    /// <summary>Tên header — giữ nguyên chữ gốc để hiển thị; unique NOCASE trong một provider.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Giá trị header; chuỗi rỗng hợp lệ (header không giá trị).</summary>
    public string Value { get; set; } = string.Empty;

    public Provider? Provider { get; set; }
}
