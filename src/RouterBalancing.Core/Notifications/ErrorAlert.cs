namespace RouterBalancing.Core.Notifications;

/// <summary>
/// Một cụm lỗi đã gom nhóm, sẵn sàng hiển thị cho người dùng (toast trong app
/// hoặc notification desktop). Title/Message đã qua i18n và cắt ký tự.
/// </summary>
/// <param name="Count">Số lỗi trong cụm (≥ 1).</param>
/// <param name="Title">Tiêu đề thông báo (i18n).</param>
/// <param name="Message">Nội dung thông báo (i18n, đã cắt theo <see cref="ErrorAlertMessage.MaxMessageChars"/>).</param>
/// <param name="FirstAt">Thời điểm lỗi đầu tiên trong cụm.</param>
/// <param name="LastAt">Thời điểm lỗi gần nhất trong cụm.</param>
public sealed record ErrorAlert(
    int Count,
    string Title,
    string Message,
    DateTimeOffset FirstAt,
    DateTimeOffset LastAt);
