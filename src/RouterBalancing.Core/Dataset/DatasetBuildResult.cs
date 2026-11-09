namespace RouterBalancing.Core.Dataset;

/// <summary>
/// Kết quả của <see cref="DatasetRecordBuilder.Build"/> — đúng một trong hai: dòng JSONL schema v:1
/// sẵn sàng ghi (<see cref="JsonLine"/>, không kèm newline cuối — writer tự thêm) hoặc quyết định
/// bỏ record kèm lý do (<see cref="Skip"/>, spec §7.6).
/// </summary>
/// <param name="JsonLine">Dòng JSON v:1; <see langword="null"/> khi record bị bỏ.</param>
/// <param name="Skip">Lý do bỏ record; <see langword="null"/> khi ghi.</param>
public sealed record DatasetBuildResult(string? JsonLine, DatasetSkipReason? Skip);
