using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;

namespace RouterBalancing.Core.Update;

/// <summary>
/// Khôi phục <c>queue-snapshot.json</c> (được <see cref="QueueSnapshotStore"/> ghi trước khi
/// app restart cài update) vào pipeline dispatcher khi proxy khởi động (spec §8) — request
/// chưa dispatch sống sót qua restart, client retry không mất. Không có file → noop; mọi file
/// đã thấy (kể cả parse/version fail) luôn bị xoá để không lặp lỗi ở lần start sau.
/// </summary>
/// <param name="queue">Queue thật trong container proxy — request restore đi vào đây.</param>
/// <param name="monitor">API monitor — H1 <c>StartRequest</c> cho từng request restore.</param>
/// <param name="trace">Feed trace — event <c>Received</c> cho từng request restore.</param>
/// <param name="log">Log service — Warn cho file corrupt/version lạ/id trùng.</param>
public sealed class QueueSnapshotRestorer(IRequestQueue queue, IApiMonitorStore monitor,
    ITraceFeed trace, ILogService log)
{
    /// <summary>
    /// Restore toàn bộ request trong snapshot tại <paramref name="path"/> vào <c>queue</c>
    /// rồi xoá file. Từng item fail (id trùng) → skip + log, các item sau vẫn tiếp tục.
    /// </summary>
    /// <param name="path">Đường dẫn snapshot — caller truyền <c>StoragePathProvider.GetQueueSnapshotPath()</c>.</param>
    /// <param name="ct">Token hủy — thao tác hiện đều ngắn (đọc file nhỏ + enqueue) nên chưa kiểm tra giữa chừng; giữ chữ ký đồng nhất call site.</param>
    public async Task RestoreAsync(string path, CancellationToken ct = default)
    {
        QueueSnapshot? snapshot;
        try
        {
            snapshot = await QueueSnapshotStore.LoadAsync(path);
        }
        catch (JsonException ex)
        {
            // File corrupt — không biết nội dung, không enqueue; xoá để lần start sau không fail lại
            log.Warn($"Queue snapshot corrupt, bỏ qua và xoá file: {ex.Message}");
            TryDeleteFile(path);
            return;
        }
        catch (NotSupportedException ex)
        {
            // Version lạ — schema không đọc được, cũng xoá để không fail lặp
            log.Warn($"Queue snapshot sai phiên bản, bỏ qua và xoá file: {ex.Message}");
            TryDeleteFile(path);
            return;
        }

        if (snapshot is null)
            return; // file chưa từng ghi — noop

        foreach (var item in snapshot.Requests)
        {
            var priority = (RequestPriority)item.Priority;
            var endpoint = Enum.Parse<ProxyEndpoint>(item.Endpoint, ignoreCase: true);

            // HttpContext surrogate: handler ghi response vào Stream.Null là vô hại.
            // RequestAborted = None (không bao giờ fire) → không đăng ký RequestAborted (spec §8)
            var ctx = new DefaultHttpContext();
            ctx.Response.Body = Stream.Null;
            ctx.RequestAborted = CancellationToken.None;
            ctx.Response.StatusCode = 200;
            var body = Convert.FromBase64String(item.BodyBase64);
            var request = new ProxyRequest(item.Id, priority, item.Model, body, ctx, endpoint);

            if (item.IsStream)
            {
                // Pipe vào Items TRƯỚC Enqueue — đúng luật two-writer (spec §2.3): slot trống
                // thì dispatcher có thể Take + serve ngay; không có endpoint thật nên drain
                // task tự tiêu hoá byte thay cho client (client sẽ re-execute sau update)
                var pipe = new Pipe();
                ctx.Items[SsePipeItems.Key] = pipe;
                _ = DrainPipeAsync(pipe, request.Completion.Task);
            }

            if (!queue.Enqueue(request))
            {
                // Gần như không xảy ra (file chỉ ghi 1 lần) — không được nuốt im lặng
                log.Warn($"Bỏ qua request {item.Id} trong queue snapshot — id đã tồn tại.");
                continue;
            }

            // RequestQueue.Enqueue ghi đè EnqueuedAt = UtcNow — PHẢI set lại SAU Enqueue
            // (Review Focus #3): không thì mọi request restore có timestamp "now", mất
            // đúng thứ tự/priority aging đã ghi trong snapshot.
            request.EnqueuedAt = item.EnqueuedAt;

            // H1 như request thường — monitor/trace không phân biệt restore hay live
            monitor.StartRequest(item.Id, item.Model, body);
            trace.Publish(new TraceEvent(item.Id, TraceStage.Received, item.Model,
                null, null, null, null, null, DateTimeOffset.Now,
                Priority: request.Priority, Endpoint: item.Endpoint));
        }

        // Đã thấy và xử lý xong toàn bộ file (kể cả có item fail) → xoá: request đã
        // vào queue, đọc lại lần sau chỉ sinh id trùng bị skip
        TryDeleteFile(path);
    }

    /// <summary>
    /// Fire-and-forget: đọc và vứt byte từ pipe surrogate tới khi handler Complete writer
    /// hoặc request kết thúc — surrogate không có client nên không ai forward ra wire.
    /// </summary>
    /// <param name="pipe">Pipe đã publish vào <c>ctx.Items[SsePipeItems.Key]</c>.</param>
    /// <param name="completion">Task outcome của request — điều kiện thoát khi không còn byte nào tới.</param>
    private async Task DrainPipeAsync(Pipe pipe, Task completion)
    {
        try
        {
            while (true)
            {
                var readTask = pipe.Reader.ReadAsync().AsTask();
                if (await Task.WhenAny(readTask, completion) == completion)
                {
                    // Request đã kết thúc mà handler có thể không kịp Complete writer —
                    // huỷ read đang pending để thoát; await để quan sát kết quả, không nuốt exception
                    pipe.Reader.CancelPendingRead();
                    _ = await readTask;
                    return;
                }

                var result = await readTask;
                pipe.Reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted || result.IsCanceled)
                    return;
            }
        }
        catch (Exception ex)
        {
            // Drain là fire-and-forget — rơi ra ngoài sẽ thành unobserved task exception
            log.Warn($"Drain pipe surrogate lỗi: {ex.Message}");
        }
        finally
        {
            // Reader completer duy nhất của pipe — không Complete thì pipe treo vĩnh viễn
            pipe.Reader.Complete();
        }
    }

    /// <summary>
    /// Xoá file snapshot sau khi đã thấy — file tồn tại ở lần start sau sẽ bị restore
    /// lần nữa (id trùng bị skip vô ích) hoặc fail lặp nếu file hỏng. Xoá fail chỉ log:
    /// lần start sau tự xử lý lại.
    /// </summary>
    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            log.Warn($"Không xoá được queue snapshot: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            log.Warn($"Không xoá được queue snapshot: {ex.Message}");
        }
    }
}
