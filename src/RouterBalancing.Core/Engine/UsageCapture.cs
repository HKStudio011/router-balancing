using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Dataset;
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>Clock cho deadline/polling sau EOF; test có thể dùng deterministic implementation.</summary>
internal interface IGraceClock
{
    long GetTimestamp();
    TimeSpan GetElapsedTime(long timestamp);
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemGraceClock : IGraceClock
{
    public static readonly SystemGraceClock Instance = new();

    private SystemGraceClock() { }

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long timestamp) => Stopwatch.GetElapsedTime(timestamp);

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

/// <summary>
/// Đọc usage token từ upstream response (spec client-keys §6):
/// (1) <see cref="WithIncludeUsage"/> chèn <c>stream_options.include_usage=true</c> cho stream OpenAI;
/// (2) <see cref="TeeAsync"/> copy body sang client MÀ không nuốt/chỉnh sửa byte — vừa forward vừa quét usage.
/// </summary>
// Public vì IProxyProtocol.TeeAsync (public seam, spec v1-responses §3.1) trả UsageCapture.TeeResult
// — chữ ký public không được lộ type internal.
public static class UsageCapture
{
    // Cap ResponseBody monitor = 64KB — không cap thì row DB phình theo upstream (spec api-monitor §2)
    private const int BodyCapChars = 64 * 1024;
    private const string TruncatedMarker = "[truncated]";

    /// <summary>Usage đã đọc được từ response (OpenAI-shape prompt_tokens/completion_tokens).</summary>
    public sealed record Usage(int PromptTokens, int CompletionTokens);

    /// <summary>
    /// Kết quả tee: usage đã parse + metadata cho monitor panel (spec api-monitor §3.3).
    /// </summary>
    /// <param name="Usage">Usage đọc được; <see langword="null"/> nếu không có hoặc JSON hỏng.</param>
    /// <param name="FirstTokenAt">Thời điểm byte upstream đầu tiên về — chỉ SSE;
    /// non-stream luôn <see langword="null"/> (không có khái niệm token đầu).</param>
    /// <param name="ResponseBody">Thân response đã buffer cho monitor: SSE ghép payload dòng
    /// <c>data:</c> (bỏ <c>[DONE]</c>), non-stream là toàn thân; đã cap <c>64 * 1024</c> ký tự
    /// + marker <c>[truncated]</c> nếu dài hơn.</param>
    /// <param name="EndedWithoutTerminal">SSE kết thúc SẠCH (EOF) mà không gặp <c>[DONE]</c>/
    /// <c>finish_reason</c> — upstream tự đóng stream giữa chừng (server-side cancel/swap...),
    /// không phải lỗi mạng; caller chỉ dùng để log dấu vết chẩn đoán.</param>
    /// <param name="CompletionJson">Chỉ khi tee với <c>captureCompletion: true</c> (dataset export):
    /// NGUYÊN object <c>chat.completion</c> gộp từ stream SSE (hoặc thân JSON non-stream) —
    /// không cap 64KB như <paramref name="ResponseBody"/>; <see langword="null"/> khi không bật
    /// capture, khi stream overflow budget, hoặc protocol chưa hỗ trợ.</param>
    /// <param name="CompletionComplete"><c>false</c> khi completion không tin cậy: stream cụt
    /// (thiếu <c>[DONE]</c>/<c>finish_reason</c>) hoặc chạm budget tích lũy — caller phải bỏ
    /// record thay vì dùng completion dở dang.</param>
    public sealed record TeeResult(Usage? Usage, DateTimeOffset? FirstTokenAt, string? ResponseBody,
        bool EndedWithoutTerminal = false, string? CompletionJson = null, bool CompletionComplete = true)
    {
        /// <summary>Khoảng lặng từ byte cuối tới EOF; chỉ có giá trị khi EOF thiếu terminal.</summary>
        public TimeSpan? LastByteGap { get; init; }

        /// <summary>Hết grace hữu hạn mà upstream vẫn không có byte mới hoặc terminal.</summary>
        public bool TerminalWaitTimedOut { get; init; }
    }

    /// <summary>
    /// Trả body upstream (inject <c>stream_options.include_usage</c> nếu đủ điều kiện) + cờ
    /// <c>ExpectsUsage</c> (true = đã yêu cầu usage → thiếu là bất thường, caller log Debug).
    /// </summary>
    public static (byte[] Body, bool ExpectsUsage) WithIncludeUsage(byte[] body, ProviderType providerType)
    {
        // Chỉ upstream OpenAI-compatible hiểu stream_options; Anthropic contract khác (spec §6)
        if (providerType != ProviderType.OpenAI) return (body, false);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(Encoding.UTF8.GetString(body));
        }
        catch (JsonException)
        {
            // Body không phải JSON hợp lệ — để validator/upstream xử lý, không đụng vào
            return (body, false);
        }

        if (node is not JsonObject obj) return (body, false);

        var isStream = obj.TryGetPropertyValue("stream", out var streamNode)
            && streamNode is JsonValue streamValue
            && streamValue.TryGetValue<bool>(out var flag)
            && flag;
        if (!isStream) return (body, false);

        var options = obj.TryGetPropertyValue("stream_options", out var optionsNode)
            && optionsNode is JsonObject existing
            ? existing
            : new JsonObject();
        options["include_usage"] = true;
        obj["stream_options"] = options;
        // ToJsonString escape unicode nhưng JSON escape vẫn semantic-equivalent — upstream parse y hệt
        return (Encoding.UTF8.GetBytes(obj.ToJsonString()), true);
    }

    /// <summary>
    /// Forward toàn bộ response sang <paramref name="dest"/> và trả <see cref="TeeResult"/> —
    /// byte-forward cho client giữ nguyên tuyệt đối (ghi nguyên chunk như đọc được, không sửa).
    /// SSE → ghi từng chunk ngay, scan dòng theo byte (dòng có thể cắt giữa 2 chunk);
    /// loại khác (JSON non-stream) → buffer, parse, rồi ghi tiếp — spec §6.1.
    /// </summary>
    /// <param name="captureCompletion">Bật để gộp thêm completion đầy đủ cho dataset export
    /// (<see cref="TeeResult.CompletionJson"/>); không đổi gì ở byte client nhận.</param>
    public static async Task<TeeResult> TeeAsync(HttpContent source, Stream dest, CancellationToken ct,
        bool captureCompletion = false)
    {
        var stream = await source.ReadAsStreamAsync(ct);
        var mediaType = source.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            return await TeeSseAsync(stream, dest, ct, captureCompletion);

        var bytes = new MemoryStream();
        try
        {
            await stream.CopyToAsync(bytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Buffer-only — dest chưa nhận byte nào nên mọi lỗi đọc đều là pre-content:
            // marker cho handler failover/retry thay vì abort request (xem exception)
            throw new UpstreamDiedBeforeContentException(ex);
        }
        var payload = bytes.ToArray();
        await dest.WriteAsync(payload, ct);
        var body = Encoding.UTF8.GetString(payload);
        // ResponseBody monitor vẫn cap 64KB như cũ; CompletionJson (dataset) giữ NGUYÊN THÂN
        // không cắt — record quá lớn do builder bỏ theo rule MaxExportFieldBytes, không phải ở đây.
        return new TeeResult(ParseUsage(payload), null,
            body.Length > BodyCapChars ? body[..BodyCapChars] + TruncatedMarker : body,
            CompletionJson: captureCompletion ? body : null);
    }

    /// <summary>Tee với grace sau EOF chỉ cho nhánh SSE; JSON/non-SSE dùng behavior thông thường.</summary>
    internal static async Task<TeeResult> TeeWithEofGraceAsync(HttpContent source, Stream dest,
        TimeSpan gracePeriod, CancellationToken ct, bool captureCompletion = false)
    {
        if (string.Equals(source.Headers.ContentType?.MediaType, "text/event-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            var stream = await source.ReadAsStreamAsync(ct);
            return await TeeSseAsync(stream, dest, ct, captureCompletion, gracePeriod);
        }

        return await TeeAsync(source, dest, ct, captureCompletion);
    }

    private static readonly TimeSpan GracePollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Tee SSE nguyên byte và quản lý grace sau EOF thiếu terminal: legacy khi <paramref name="gracePeriod"/>
    /// là <see langword="null"/>, chờ vô hạn với <see cref="Timeout.InfiniteTimeSpan"/>, hoặc timeout
    /// sau grace hữu hạn. Polling cố định 1 giây và không replay request.
    /// </summary>
    /// <param name="captureCompletion">Bật aggregation cho dataset; completion cụt vẫn bị loại.</param>
    /// <param name="gracePeriod">Grace sau EOF; null = immediate-truncation behavior của caller cũ.</param>
    /// <param name="graceClock">Clock monotonic/delay để test deadline mà không phụ thuộc wall-clock.</param>
    internal static async Task<TeeResult> TeeSseAsync(Stream source, Stream dest, CancellationToken ct,
        bool captureCompletion = false, TimeSpan? gracePeriod = null, IGraceClock? graceClock = null)
    {
        if (gracePeriod is { } configured && configured != Timeout.InfiniteTimeSpan
            && configured <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(gracePeriod),
                "Use Timeout.InfiniteTimeSpan for an infinite grace period.");

        graceClock ??= SystemGraceClock.Instance;
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var parser = new SseDataEventParser();
        long lastByteTimestamp = 0;
        Usage? usage = null;
        var body = new StringBuilder();
        var bodyTruncated = false;
        DateTimeOffset? firstTokenAt = null;
        var terminal = false;
        long? graceStartedAt = null;
        var terminalWaitTimedOut = false;
        // Budget = MaxExportFieldBytes của dataset: chạm budget thì ngừng tích lũy (Overflowed) —
        // completion bị bỏ, nhưng byte client vẫn forward nguyên vẹn (không cắt ở đây).
        var capture = captureCompletion
            ? new ChatSseAggregateState(DatasetRecordBuilder.MaxExportFieldBytes, budgetInUtf8Bytes: true)
            : null;
        try
        {
            var wroteAny = false;
            while (true)
            {
                if (graceStartedAt is { } started)
                {
                    var elapsed = graceClock.GetElapsedTime(started);
                    if (gracePeriod == Timeout.InfiniteTimeSpan)
                    {
                        await graceClock.Delay(GracePollInterval, ct);
                    }
                    else if (gracePeriod is { } finite && elapsed < finite)
                    {
                        var remaining = finite - elapsed;
                        await graceClock.Delay(
                            remaining < GracePollInterval ? remaining : GracePollInterval, ct);
                    }
                    ct.ThrowIfCancellationRequested();
                }

                int read;
                try
                {
                    read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                }
                catch (Exception ex) when (!wroteAny && ex is not OperationCanceledException)
                {
                    // Chết trước byte đầu tới client → marker failover/retry;
                    // đã có byte trên wire (wroteAny) → giữ IOException gốc (mid-content không retry)
                    throw new UpstreamDiedBeforeContentException(ex);
                }
                if (read > 0)
                {
                    // Byte mới hủy grace hiện tại; EOF sau đó bắt đầu một grace mới.
                    graceStartedAt = null;
                    firstTokenAt ??= DateTimeOffset.UtcNow;
                    lastByteTimestamp = graceClock.GetTimestamp();
                    await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                    wroteAny = true;
                    foreach (var dataEvent in parser.HandleChunk(buffer.AsSpan(0, read)))
                        ProcessEvent(dataEvent);
                    continue;
                }

                if (terminal) break;

                // Peek không tiêu hủy parser: tail non-terminal chưa được commit nếu còn grace.
                var finalEvent = parser.PeekFinish();
                if (finalEvent is not null)
                {
                    var tailTerminal = terminal;
                    _ = ScanSseLine(finalEvent, usage, ref tailTerminal);
                    if (tailTerminal || gracePeriod is null || !wroteAny)
                        ProcessEvent(finalEvent);
                }

                if (terminal) break;
                if (!wroteAny)
                    throw new UpstreamDiedBeforeContentException(new UpstreamTruncatedException());
                if (gracePeriod is null) break;

                if (graceStartedAt is null)
                {
                    graceStartedAt = graceClock.GetTimestamp();
                    continue;
                }

                if (DeadlineReached())
                {
                    terminalWaitTimedOut = true;
                    break;
                }
            }

            var completionJson = capture is { Overflowed: false } ? capture.BuildCompletionJson() : null;
            var completionComplete = !terminalWaitTimedOut
                && (capture is null || (capture.CompletedCleanly && !capture.Overflowed));
            if (!terminal)
            {
                if (!wroteAny)
                    throw new UpstreamDiedBeforeContentException(new UpstreamTruncatedException());

                return new TeeResult(usage, firstTokenAt, body.ToString(),
                    EndedWithoutTerminal: true, CompletionJson: completionJson,
                    CompletionComplete: completionComplete)
                {
                    LastByteGap = graceClock.GetElapsedTime(lastByteTimestamp),
                    TerminalWaitTimedOut = terminalWaitTimedOut,
                };
            }
            return new TeeResult(usage, firstTokenAt, body.ToString(),
                CompletionJson: completionJson, CompletionComplete: completionComplete);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        void ProcessEvent(byte[] dataEvent)
        {
            usage = ScanSseLine(dataEvent, usage, ref terminal);
            AppendSsePayload(dataEvent, body, ref bodyTruncated);
            capture?.HandleLine(dataEvent);
        }

        bool DeadlineReached() => graceStartedAt is { } started
            && gracePeriod is { } finite
            && finite != Timeout.InfiniteTimeSpan
            && graceClock.GetElapsedTime(started) >= finite;
    }


    // Tích payload dòng "data:" (bỏ 1 space sau ':' theo SSE spec, bỏ [DONE]) làm ResponseBody cho monitor.
    // Vượt cap thì cắt + gắn marker đúng 1 lần rồi ngừng tích — body cuối luôn ≤ cap, không phình vô hạn
    // dù upstream gửi một dòng khổng lồ.
    private static void AppendSsePayload(ReadOnlySpan<byte> line, StringBuilder body, ref bool truncated)
    {
        if (truncated || !line.StartsWith("data:"u8)) return;
        var payload = line["data:"u8.Length..];
        if (!payload.IsEmpty && payload[0] == (byte)' ') payload = payload[1..];
        if (payload.SequenceEqual("[DONE]"u8)) return;
        if (body.Length > 0) body.Append('\n');
        body.Append(Encoding.UTF8.GetString(payload));
        if (body.Length <= BodyCapChars) return;
        // Marker SSE nối bằng "\n" nên content cắt tại cap-1 → tổng = 64*1024 + "[truncated]" (cap global)
        body.Length = BodyCapChars - 1;
        body.Append('\n').Append(TruncatedMarker);
        truncated = true;
    }

    // Chỉ dòng "data: {...}" chứa usage hợp lệ; JSON hỏng → bỏ qua dòng đó, không nổ stream.
    // Guard ValueKind Number trước GetInt32: field sai kiểu (string/bool) ném InvalidOperationException;
    // số hợp lệ nhưng vượt phạm vi Int32 (vd prompt_tokens: 1e15) ném FormatException — cả hai
    // đều KHÔNG phải JsonException, nên catch mở rộng bắt đủ (spec §10: usage parse lỗi không
    // được throw làm hỏng request).
    // Đồng thời đánh dấu `terminal` khi gặp [DONE] hoặc finish_reason khác null — thiếu cả hai
    // tại EOF sạch nghĩa là upstream cắt cụt (TeeSseAsync throw).
    private static Usage? ScanSseLine(ReadOnlySpan<byte> line, Usage? current, ref bool terminal)
    {
        if (!line.StartsWith("data:"u8)) return current;
        var payload = line["data:"u8.Length..];
        // SSE cho phép 1 space sau ':' — thiếu trim thì "data: [DONE]" không khớp
        if (!payload.IsEmpty && payload[0] == (byte)' ') payload = payload[1..];
        if (payload.SequenceEqual("[DONE]"u8))
        {
            terminal = true;
            return current;
        }
        try
        {
            // net10 không còn overload Parse(ReadOnlySpan<byte>) — ToArray() sang ReadOnlyMemory<byte>
            using var doc = JsonDocument.Parse(payload.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return current;
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("finish_reason", out var finish)
                && finish.ValueKind == JsonValueKind.String)
                terminal = true;
            if (!doc.RootElement.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object
                || !usage.TryGetProperty("prompt_tokens", out var prompt)
                || prompt.ValueKind != JsonValueKind.Number
                || !usage.TryGetProperty("completion_tokens", out var completion)
                || completion.ValueKind != JsonValueKind.Number)
                return current;
            // last-wins: nhiều chunk usage → lấy cái cuối (spec §6.1)
            return new Usage(prompt.GetInt32(), completion.GetInt32());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Lỗi parse/chuyển đổi số → bỏ dòng, giữ usage cũ (fail-open)
            return current;
        }
    }

    private static Usage? ParseUsage(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("prompt_tokens", out var prompt)
                && prompt.ValueKind == JsonValueKind.Number
                && usage.TryGetProperty("completion_tokens", out var completion)
                && completion.ValueKind == JsonValueKind.Number)
                return new Usage(prompt.GetInt32(), completion.GetInt32());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Lỗi parse/chuyển đổi số → không có usage, request vẫn thành công (spec §10).
            // Filter mở rộng vì GetInt32 trên JSON Number vượt phạm vi Int32 (vd prompt_tokens: 1e15)
            // ném FormatException, field sai kiểu ném InvalidOperationException — ĐỀU không phải
            // JsonException; bắt thiếu là fail-open hole vì body đã ghi dest trước khi ParseUsage chạy.
        }
        return null;
    }
}
