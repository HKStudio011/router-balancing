namespace RouterBalancing.Core.Engine;

/// <summary>
/// Key trong <c>HttpContext.Items</c> mà endpoint đặt <see cref="System.IO.Pipelines.Pipe"/>
/// của response SSE tại flush point (spec early-headers §3.2): handler 2xx đọc key này để
/// lấy <c>pipe.Writer</c> làm dest thay vì viết thẳng <c>Response.Body</c> — endpoint là tay
/// duy nhất ghi <c>Response.Body</c> và là completer duy nhất của pipe.
/// </summary>
internal static class SsePipeItems
{
    /// <summary>
    /// Value là <see cref="System.IO.Pipelines.Pipe"/> — chỉ được set cho request
    /// <c>stream=true</c> sau khi qua enqueue; absence = đường non-stream cũ.
    /// </summary>
    public const string Key = "__RouterBalancing.SsePipe";
}
