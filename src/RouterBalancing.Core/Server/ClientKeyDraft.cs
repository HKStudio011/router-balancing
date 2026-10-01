namespace RouterBalancing.Core.Server;

/// <summary>Dữ liệu tạo/sửa client key — không chứa plaintext (key mới chỉ sinh ngẫu nhiên).</summary>
public sealed record ClientKeyDraft(string Name, int? RatePerMinute, int? TokensPerMinute);
