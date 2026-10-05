using System.Text;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Thay body request bằng bytes mới, copy content header cũ ngoại trừ Content-Length.
/// Copy nguyên Content-Length của body cũ làm hứa sai độ dài khi transform đổi bytes →
/// <c>HttpRequestException "Sent N bytes, but Content-Length promised M"</c> lúc gửi thật
/// (bắt qua harness 2026-10-05 — unit test chỉ đọc content nên không thấy);
/// <see cref="ByteArrayContent"/> tự tính lại theo bytes mới.
/// </summary>
internal static class ContentReplacer
{
    /// <param name="request">Request đang cầm content cũ.</param>
    /// <param name="oldContent">Body cũ — được thay và Dispose.</param>
    /// <param name="newBytes">Body mới sau transform.</param>
    public static void Replace(HttpRequestMessage request, HttpContent oldContent, byte[] newBytes)
    {
        var replacement = new ByteArrayContent(newBytes);
        foreach (var header in oldContent.Headers)
        {
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        request.Content = replacement;
        oldContent.Dispose();
    }

    /// <inheritdoc cref="Replace"/>
    public static void Replace(HttpRequestMessage request, HttpContent oldContent, string newBody) =>
        Replace(request, oldContent, Encoding.UTF8.GetBytes(newBody));
}
