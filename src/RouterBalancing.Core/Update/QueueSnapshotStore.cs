using System.Text.Json;
using System.Text.Json.Serialization;
using RouterBalancing.Core.Engine;

namespace RouterBalancing.Core.Update;

/// <summary>
/// Snapshot hàng đợi — toàn bộ request chưa dispatch tại thời điểm app sắp restart
/// cài update; <c>QueueSnapshotRestorer</c> đọc lại sau khi khởi động (spec §8).
/// </summary>
/// <param name="Version">Phiên bản schema — khác <see cref="QueueSnapshotStore.CurrentVersion"/> là file không đọc được.</param>
/// <param name="SavedAt">Thời điểm ghi snapshot (UTC).</param>
/// <param name="Requests">Request theo đúng thứ tự enqueue tại thời điểm ghi.</param>
public sealed record QueueSnapshot(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("saved_at")] DateTimeOffset SavedAt,
    [property: JsonPropertyName("requests")] List<QueueSnapshotItem> Requests);

/// <summary>
/// Một request trong snapshot — HttpContext/body stream không sống qua restart nên chỉ
/// chụp lại giá trị thuần cần để tái tạo <see cref="ProxyRequest"/>.
/// </summary>
/// <param name="Id">Mã request gốc — giữ nguyên để monitor/trace liên tục qua restart.</param>
/// <param name="Priority">Mức ưu tiên dạng số (giá trị của <see cref="RequestPriority"/>).</param>
/// <param name="Model">Tên model/combo gốc client gửi.</param>
/// <param name="BodyBase64">Body JSON đã buffer — base64 để an toàn khi qua JSON.</param>
/// <param name="Endpoint">Đích protocol dạng chữ thường (<c>chat</c>/<c>responses</c>).</param>
/// <param name="IsStream">
/// Request stream hay không — phát hiện qua sự hiện diện của <c>SsePipeItems.Key</c> trong
/// <c>HttpContext.Items</c>; restorer tái tạo pipe tương ứng cho item này.
/// </param>
/// <param name="EnqueuedAt">Thời điểm vào queue — giữ để thứ tự/priority aging không mất sau restart.</param>
public sealed record QueueSnapshotItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("body_base64")] string BodyBase64,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("is_stream")] bool IsStream,
    [property: JsonPropertyName("enqueued_at")] DateTimeOffset EnqueuedAt);

/// <summary>
/// Ghi/đọc snapshot hàng đợi ra file JSON — cầu nối để request chưa dispatch sống sót
/// qua restart cài update (spec §8). Ghi atomic (file tmp rồi move) để app crash giữa chừng
/// không bao giờ để lại file đích viết dở; caller xử lý mọi lỗi ghi/đọc, store không nuốt.
/// </summary>
public static class QueueSnapshotStore
{
    /// <summary>Phiên bản schema hiện hành — file khác phiên bản bị ném <see cref="NotSupportedException"/> để restorer tự xoá.</summary>
    public const int CurrentVersion = 1;

    // WriteIndented: snapshot do người dùng/nhà phát hiện đọc tay khi debug, không cần tối ưu kích thước
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// Ghi toàn bộ request đang nằm trong queue xuống <paramref name="path"/> (atomic qua file tmp).
    /// </summary>
    /// <param name="requests">Request chưa dispatch — chỉ chụp giá trị thuần, không đụng vào HttpContext gốc.</param>
    /// <param name="path">Đường dẫn đích — caller thường truyền <c>StoragePathProvider.GetQueueSnapshotPath()</c>.</param>
    /// <param name="ct">Token hủy thao tác ghi file.</param>
    /// <exception cref="IOException">Ghi file thất bại (đĩa đầy, thư mục không tồn tại…) — caller quyết định dừng update hay bỏ qua.</exception>
    /// <exception cref="NotSupportedException">Không serialize được dữ liệu request — caller xử lý như ghi fail.</exception>
    public static async Task SaveAsync(IReadOnlyList<ProxyRequest> requests, string path,
        CancellationToken ct = default)
    {
        var items = new List<QueueSnapshotItem>(requests.Count);
        foreach (var request in requests)
        {
            items.Add(new QueueSnapshotItem(
                request.Id,
                (int)request.Priority,
                request.Model,
                Convert.ToBase64String(request.Body),
                request.Endpoint.ToString().ToLowerInvariant(),
                request.Context.Items.ContainsKey(SsePipeItems.Key),
                request.EnqueuedAt));
        }

        var snapshot = new QueueSnapshot(CurrentVersion, DateTimeOffset.UtcNow, items);
        var json = JsonSerializer.Serialize(snapshot, Options);

        // Ghi tmp rồi move: đọc file giữa chừng ghi (crash) luôn thấy bản cũ hoàn chỉnh
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Đọc snapshot từ <paramref name="path"/>.
    /// </summary>
    /// <returns>Snapshot đã parse; <see langword="null"/> khi file chưa tồn tại (chưa từng ghi snapshot).</returns>
    /// <exception cref="JsonException">File corrupt hoặc rỗng — caller (restorer) xoá để không fail lặp ở lần khởi động sau.</exception>
    /// <exception cref="NotSupportedException">File thuộc phiên bản schema khác <see cref="CurrentVersion"/> — caller cũng xoá.</exception>
    public static async Task<QueueSnapshot?> LoadAsync(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path);
        var snapshot = JsonSerializer.Deserialize<QueueSnapshot>(json, Options);
        if (snapshot is null)
        {
            throw new JsonException($"Snapshot rỗng tại {path}");
        }

        if (snapshot.Version != CurrentVersion)
        {
            throw new NotSupportedException(
                $"Snapshot phiên bản {snapshot.Version} không khớp phiên bản hiện tại {CurrentVersion}.");
        }

        return snapshot;
    }
}
