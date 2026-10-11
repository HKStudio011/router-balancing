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
/// đã thấy LUÔN bị xoá sau khi xử lý (kể cả corrupt/version lạ/lỗi bất ngờ) và
/// <see cref="RestoreAsync"/> không bao giờ nổ exception cho file đã thấy — wiring nằm trước
/// try StartAsync của <c>ProxyHost</c>, exception lọt ra ngoài là crash startup.
/// </summary>
/// <param name="queue">Queue thật trong container proxy — request restore đi vào đây.</param>
/// <param name="monitor">API monitor — H1 <c>StartRequest</c> cho từng request restore.</param>
/// <param name="trace">Feed trace — event <c>Received</c> cho từng request restore.</param>
/// <param name="log">Log service — Warn cho file corrupt/version lạ/item fail.</param>
public sealed class QueueSnapshotRestorer(IRequestQueue queue, IApiMonitorStore monitor,
    ITraceFeed trace, ILogService log)
{
    /// <summary>
    /// Restore toàn bộ request trong snapshot tại <paramref name="path"/> vào <c>queue</c>.
    /// File thiếu → noop. File đã thấy → luôn xoá sau khi xử lý; từng item fail (id trùng,
    /// base64 hỏng, enum lạ…) → skip item + log Warn, các item sau vẫn tiếp tục; lỗi bất
    /// ngờ ngoài per-item → log Warn, không nổ ra caller.
    /// </summary>
    /// <param name="path">Đường dẫn snapshot — caller truyền <c>StoragePathProvider.GetQueueSnapshotPath()</c>.</param>
    /// <param name="ct">Token hủy — thao tác hiện đều ngắn (đọc file nhỏ + enqueue) nên chưa kiểm tra giữa chừng; giữ chữ ký đồng nhất call site.</param>
    public async Task RestoreAsync(string path, CancellationToken ct = default)
    {
        // Check exists trước: qua dòng này file được coi "đã thấy" → trách nhiệm xoá
        // (LoadAsync cũng check + trả null — gộp về một nhánh duy nhất cho dễ reason)
        if (!File.Exists(path))
            return;

        try
        {
            QueueSnapshot? snapshot;
            try
            {
                snapshot = await QueueSnapshotStore.LoadAsync(path);
            }
            catch (JsonException ex)
            {
                // File corrupt — không biết nội dung, không enqueue; finally vẫn xoá
                log.Warn($"Queue snapshot corrupt, bỏ qua: {ex.Message}");
                return;
            }
            catch (NotSupportedException ex)
            {
                // Version lạ — schema không đọc được; finally vẫn xoá
                log.Warn($"Queue snapshot sai phiên bản, bỏ qua: {ex.Message}");
                return;
            }

            if (snapshot is null)
                return;

            foreach (var item in snapshot.Requests)
            {
                try
                {
                    RestoreItem(item);
                }
                catch (Exception ex)
                {
                    // Ruling/spec §8: item fail (base64 hỏng, enum lạ, id trùng…) → skip
                    // item đó + log, không để 1 item hỏng chặn các item còn lại của file
                    log.Warn($"Bỏ qua item {item.Id} trong queue snapshot: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // Lỗi bất ngờ ngoài per-item (Requests null, đọc file dở…) — KHÔNG được nổ
            // ra ngoài: RestoreAsync chạy trước try StartAsync của ProxyHost (spec §8),
            // exception = crash startup ngay sau khi app vừa được cài update xong
            log.Warn($"Lỗi không mong đợi khi restore queue snapshot: {ex.Message}");
        }
        finally
        {
            // File đã thấy → LUÔN xoá sau khi xử lý (thành công, corrupt, version lạ
            // hay lỗi bất ngờ): đọc lại lần sau chỉ sinh id trùng bị skip vô ích,
            // hoặc fail lặp nếu file hỏng
            TryDeleteFile(path);
        }
    }

    /// <summary>
    /// Tái tạo 1 <see cref="ProxyRequest"/> surrogate từ item snapshot rồi enqueue +
    /// đồng bộ monitor/trace (H1). Throw ra caller nếu item hỏng — caller (per-item
    /// catch trong <see cref="RestoreAsync"/>) skip + log.
    /// </summary>
    private void RestoreItem(QueueSnapshotItem item)
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
            return;
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
