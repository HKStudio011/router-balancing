using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Storage;
using RouterBalancing.Core.Update;

namespace router_balancing_test.Update;

public class QueueSnapshotStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"queue-snapshot-{Guid.NewGuid():N}");

    private string SnapshotPath => Path.Combine(_directory, "queue-snapshot.json");

    public QueueSnapshotStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsFields()
    {
        var highest = CreateRequest("req-aaaaaa", RequestPriority.Highest, "gpt-5",
            """{"messages":[{"role":"user","content":"hi"}]}""", ProxyEndpoint.Chat);
        highest.EnqueuedAt = new DateTimeOffset(2026, 10, 11, 8, 30, 0, TimeSpan.FromHours(7));
        var normal = CreateRequest("req-bbbbbb", RequestPriority.Normal, "claude-sonnet-4-5",
            """{"model":"claude-sonnet-4-5","stream":false}""", ProxyEndpoint.Responses);
        normal.EnqueuedAt = new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

        await QueueSnapshotStore.SaveAsync([highest, normal], SnapshotPath);
        var snapshot = await QueueSnapshotStore.LoadAsync(SnapshotPath);

        Assert.NotNull(snapshot);
        Assert.Equal(QueueSnapshotStore.CurrentVersion, snapshot.Version);
        Assert.Equal(2, snapshot.Requests.Count);

        var first = snapshot.Requests[0];
        Assert.Equal("req-aaaaaa", first.Id);
        Assert.Equal((int)RequestPriority.Highest, first.Priority);
        Assert.Equal("gpt-5", first.Model);
        Assert.Equal(highest.Body, Convert.FromBase64String(first.BodyBase64));
        Assert.Equal("chat", first.Endpoint);
        Assert.Equal(highest.EnqueuedAt, first.EnqueuedAt);

        var second = snapshot.Requests[1];
        Assert.Equal("req-bbbbbb", second.Id);
        Assert.Equal((int)RequestPriority.Normal, second.Priority);
        Assert.Equal("claude-sonnet-4-5", second.Model);
        Assert.Equal(normal.Body, Convert.FromBase64String(second.BodyBase64));
        Assert.Equal("responses", second.Endpoint);
        Assert.Equal(normal.EnqueuedAt, second.EnqueuedAt);
    }

    [Fact]
    public async Task SaveAsync_MarksStreamRequest_WhenPipeKeyPresent()
    {
        var streamRequest = CreateRequest("req-stream", RequestPriority.Normal, "gpt-5", "{}");
        streamRequest.Context.Items[SsePipeItems.Key] = new Pipe();
        var plainRequest = CreateRequest("req-plain", RequestPriority.Normal, "gpt-5", "{}");

        await QueueSnapshotStore.SaveAsync([streamRequest, plainRequest], SnapshotPath);
        var snapshot = await QueueSnapshotStore.LoadAsync(SnapshotPath);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.Requests.Single(r => r.Id == "req-stream").IsStream);
        Assert.False(snapshot.Requests.Single(r => r.Id == "req-plain").IsStream);
    }

    [Fact]
    public async Task SaveAsync_WritesViaTempFile_ThenMoves()
    {
        var request = CreateRequest("req-tmp", RequestPriority.Normal, "gpt-5", "{}");

        await QueueSnapshotStore.SaveAsync([request], SnapshotPath);

        // Atomic write: file tmp phải được move đi, không còn sót lại cạnh file đích
        Assert.False(File.Exists(SnapshotPath + ".tmp"));
        Assert.True(File.Exists(SnapshotPath));
        var parsed = JsonSerializer.Deserialize<QueueSnapshot>(File.ReadAllText(SnapshotPath));
        Assert.NotNull(parsed);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_WhenFileMissing()
        => Assert.Null(await QueueSnapshotStore.LoadAsync(SnapshotPath));

    [Fact]
    public async Task LoadAsync_Throws_WhenCorruptJson()
    {
        await File.WriteAllTextAsync(SnapshotPath, "not-a-json");

        await Assert.ThrowsAsync<JsonException>(() => QueueSnapshotStore.LoadAsync(SnapshotPath));
    }

    [Fact]
    public async Task LoadAsync_Throws_WhenVersionDiffers()
    {
        var request = CreateRequest("req-ver", RequestPriority.Normal, "gpt-5", "{}");
        await QueueSnapshotStore.SaveAsync([request], SnapshotPath);
        // Version 99 mô phỏng file ghi bởi app bản khác — restorer phải xoá thay vì đọc nhầm schema
        var json = File.ReadAllText(SnapshotPath)
            .Replace($"\"version\": {QueueSnapshotStore.CurrentVersion}", "\"version\": 99");
        await File.WriteAllTextAsync(SnapshotPath, json);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            QueueSnapshotStore.LoadAsync(SnapshotPath));
    }

    [Fact]
    public void GetQueueSnapshotPath_EndsWithFileName()
        => Assert.Equal("queue-snapshot.json",
            Path.GetFileName(StoragePathProvider.GetQueueSnapshotPath()));

    private static ProxyRequest CreateRequest(string id, RequestPriority priority, string model,
        string body, ProxyEndpoint endpoint = ProxyEndpoint.Chat)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return new ProxyRequest(id, priority, model, Encoding.UTF8.GetBytes(body), ctx, endpoint);
    }
}
