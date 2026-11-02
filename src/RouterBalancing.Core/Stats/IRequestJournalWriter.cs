using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Stats;

/// <summary>
/// Marker cho journal writer: ProxyHost nhận qua ctor để eager-create singleton từ app
/// container — writer subscribe <c>ITraceFeed.Published</c> trong ctor nhưng không endpoint
/// nào inject nó nên DI không tự resolve (spec dashboard-stats §4).
/// Ngoài marker, interface còn exposes <see cref="EntryWritten"/> làm nguồn sự kiện cho
/// tính năng thông báo lỗi desktop (spec error-notify-client-name).
/// </summary>
public interface IRequestJournalWriter
{
    /// <summary>
    /// Bắn đúng một lần, ngay sau khi dòng terminal của request commit thành công vào
    /// LogEntries.
    /// Vì sao event tồn tại: request journal ghi thẳng <c>db.LogEntries.Add</c> trong
    /// <c>RequestJournalWriter.WriteTerminal</c>, không đi qua <c>ILogService.Write</c>,
    /// nên <c>ILogService.LogAdded</c> không bao giờ nổ cho dòng request — event này là
    /// nguồn duy nhất cho thông báo lỗi desktop.
    /// Hợp đồng fail-open: implementation phải cách ly từng subscriber (try/catch) —
    /// handler nổ không được truyền ngược lên luồng request.
    /// </summary>
    event Action<LogEntry>? EntryWritten;
}
