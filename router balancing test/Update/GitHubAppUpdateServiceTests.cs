using System.Net;
using RouterBalancing.Core.Update;

namespace router_balancing_test.Update;

public class GitHubAppUpdateServiceTests
{
    private const string ReleaseJson = """
        {
          "tag_name": "v1.2.3",
          "html_url": "https://github.com/HKStudio011/router-balancing/releases/tag/v1.2.3",
          "body": "Release notes",
          "assets": [
            {
              "name": "router-balancing-1.2.3-win-x64.zip",
              "browser_download_url": "https://github.com/HKStudio011/router-balancing/releases/download/v1.2.3/router-balancing-1.2.3-win-x64.zip"
            }
          ]
        }
        """;

    private const string ZipAssetName = "router-balancing-test-payload.zip";

    private static GitHubAppUpdateService ServiceWith(HttpMessageHandler handler, string currentVersion = "1.0.0.0",
        string? publicKeyBase64 = null) =>
        new(new HttpClient(handler, disposeHandler: false), currentVersion, publicKeyBase64);

    private static ReleaseInfo Release(string tag, params ReleaseAsset[] assets) =>
        new(tag, $"https://github.com/HKStudio011/router-balancing/releases/tag/{tag}", null, [.. assets]);

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsRelease_WhenSuccess()
    {
        var service = ServiceWith(new JsonHandler(ReleaseJson));

        var release = await service.CheckForUpdatesAsync();

        Assert.NotNull(release);
        Assert.Equal("v1.2.3", release.TagName);
        Assert.Equal("https://github.com/HKStudio011/router-balancing/releases/tag/v1.2.3", release.HtmlUrl);
        Assert.Equal("Release notes", release.Body);
        var asset = Assert.Single(release.Assets);
        Assert.Equal("router-balancing-1.2.3-win-x64.zip", asset.Name);
        Assert.Equal(
            "https://github.com/HKStudio011/router-balancing/releases/download/v1.2.3/router-balancing-1.2.3-win-x64.zip",
            asset.BrowserDownloadUrl);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsNull_WhenNonSuccess()
    {
        var service = ServiceWith(new StatusHandler(HttpStatusCode.InternalServerError));

        Assert.Null(await service.CheckForUpdatesAsync());
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsNull_WhenNonJsonResponse()
    {
        // Spec §12: API trả HTML/gián đoạn (rate limit page, proxy error...) —
        // deserialize ném JsonException, service phải trả null chứ không ném ra caller.
        var service = ServiceWith(new JsonHandler("<html>gateway timeout</html>"));

        Assert.Null(await service.CheckForUpdatesAsync());
    }

    [Fact]
    public void IsUpdateAvailable_True_WhenTagNewer()
    {
        var service = ServiceWith(new JsonHandler("{}"), "1.0.0.0");

        Assert.True(service.IsUpdateAvailable(Release("v1.0.1")));
    }

    [Fact]
    public void IsUpdateAvailable_False_WhenEqualOrOlder()
    {
        var service = ServiceWith(new JsonHandler("{}"), "1.0.0.0");

        Assert.False(service.IsUpdateAvailable(Release("1.0.0.0")));
        // Ít thành phần hơn nhưng bằng sau khi pad 0 — 1.0 ≡ 1.0.0.0
        Assert.False(service.IsUpdateAvailable(Release("v1.0")));
        Assert.False(service.IsUpdateAvailable(Release("v0.9.9")));
    }

    [Fact]
    public void IsUpdateAvailable_True_WhenTagShorter_Padded()
    {
        var service = ServiceWith(new JsonHandler("{}"), "1.0.9.0");

        Assert.True(service.IsUpdateAvailable(Release("v1.1")));
    }

    [Fact]
    public async Task DownloadUpdateAsync_WritesZipAndSignature_AndReportsProgress()
    {
        // 30 bytes, chunk 10 → đúng 3 lần đọc, progress tăng dần 33/66/100
        var payload = "0123456789ABCDEFGHIJ0123456789"u8.ToArray();
        using var key = new TestSigningKey();
        var release = SignedRelease(payload, key, out var signatureBase64);
        var service = ServiceWith(new SignedZipHandler(payload, signatureBase64, chunkSize: 10),
            publicKeyBase64: key.PublicKeyBase64);
        var progress = new RecordingProgress();

        var path = await service.DownloadUpdateAsync(release, progress);

        try
        {
            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
            Assert.Equal(path, service.DownloadedZipPath);
            Assert.NotNull(service.DownloadedSignaturePath);
            Assert.True(File.Exists(service.DownloadedSignaturePath));
            Assert.Equal(signatureBase64, await File.ReadAllTextAsync(service.DownloadedSignaturePath));
            Assert.NotEmpty(progress.Reports);
            Assert.Equal(100, progress.Reports[^1]);
        }
        finally
        {
            if (path is not null)
            {
                File.Delete(path);
            }
            if (service.DownloadedSignaturePath is not null)
            {
                File.Delete(service.DownloadedSignaturePath);
            }
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_ReturnsNull_WhenSignatureAssetMissing()
    {
        // Fail-closed: release không có file .sig → không tin được zip, từ chối ngay lúc tải
        var payload = "PK-zip"u8.ToArray();
        using var key = new TestSigningKey();
        var release = Release("v1.2.3", new ReleaseAsset(ZipAssetName, "https://example.com/payload.zip"));
        var service = ServiceWith(new SignedZipHandler(payload, signatureBase64: null), publicKeyBase64: key.PublicKeyBase64);

        Assert.Null(await service.DownloadUpdateAsync(release, null));
        Assert.Null(service.DownloadedZipPath);
    }

    [Fact]
    public async Task InstallUpdateAsync_FalseAndDeletesZip_WhenSignatureInvalid()
    {
        // Threat model: zip bị tamper hoặc ký sai key — verify fail → không khởi động updater, xóa zip rác
        var payload = "PK-zip"u8.ToArray();
        using var key = new TestSigningKey();
        using var otherKey = new TestSigningKey();
        var release = SignedRelease(payload, key, out var signatureBase64);
        var service = ServiceWith(new SignedZipHandler(payload, signatureBase64),
            publicKeyBase64: otherKey.PublicKeyBase64);
        var downloaded = await service.DownloadUpdateAsync(release, null);

        try
        {
            Assert.NotNull(downloaded);

            Assert.False(await service.InstallUpdateAsync());
            Assert.Null(service.DownloadedZipPath);
            Assert.False(File.Exists(downloaded));
        }
        finally
        {
            if (downloaded is not null)
            {
                File.Delete(downloaded);
            }
            if (service.DownloadedSignaturePath is not null)
            {
                File.Delete(service.DownloadedSignaturePath);
            }
        }
    }

    [Fact]
    public async Task InstallUpdateAsync_False_WhenUpdaterMissing()
    {
        // Sanity: test bin không được có sẵn updater — nếu có thì nhánh "missing" không còn đúng
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "RouterBalancing.Updater.exe")));

        var payload = "PK-zip"u8.ToArray();
        using var key = new TestSigningKey();
        var release = SignedRelease(payload, key, out var signatureBase64);
        var service = ServiceWith(new SignedZipHandler(payload, signatureBase64),
            publicKeyBase64: key.PublicKeyBase64);
        var downloaded = await service.DownloadUpdateAsync(release, null);

        try
        {
            Assert.NotNull(downloaded);

            // Signature hợp lệ → vượt qua verify, fail ở bước thiếu updater → false
            Assert.False(await service.InstallUpdateAsync());
        }
        finally
        {
            if (downloaded is not null)
            {
                File.Delete(downloaded);
            }
            if (service.DownloadedSignaturePath is not null)
            {
                File.Delete(service.DownloadedSignaturePath);
            }
        }
    }

    [Fact]
    public void BuildUpdaterStartInfo_UsesArgumentList_AndKeepsTrailingBackslash()
    {
        // Simulate AppContext.BaseDirectory — luôn kết thúc bằng '\'
        const string zipPath = @"C:\Temp\RouterBalancing_Update\app.zip";
        const string appDir = @"C:\Program Files\RouterBalancing\net10.0\";

        var psi = GitHubAppUpdateService.BuildUpdaterStartInfo(zipPath, 1234, appDir);

        // Sequence đúng contract parser của updater: [--zip, zip, --pid, pid, --app-dir, dir]
        Assert.Equal(
            new[] { "--zip", zipPath, "--pid", "1234", "--app-dir", appDir },
            psi.ArgumentList);

        // Regression: '\' cuối phải giữ nguyên verbatim — chuỗi Arguments thủ công
        // sẽ bị Windows parse là escape dấu quote và updater nhận arg có ký tự '"'.
        Assert.EndsWith(@"\", psi.ArgumentList[^1]);
        Assert.Equal(string.Empty, psi.Arguments);
        Assert.True(psi.UseShellExecute);
    }

    [Fact]
    public void PrepareUpdaterShadowCopy_CopiesRuntimeFiles_SkipsPdbAndAppFiles()
    {
        var appDir = Path.Combine(Path.GetTempPath(), "rb-updater-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDir);
        try
        {
            File.WriteAllText(Path.Combine(appDir, "RouterBalancing.Updater.exe"), "exe");
            File.WriteAllText(Path.Combine(appDir, "RouterBalancing.Updater.dll"), "dll");
            File.WriteAllText(Path.Combine(appDir, "RouterBalancing.Updater.deps.json"), "{}");
            File.WriteAllText(Path.Combine(appDir, "RouterBalancing.Updater.runtimeconfig.json"), "{}");
            File.WriteAllText(Path.Combine(appDir, "RouterBalancing.Updater.pdb"), "pdb");
            File.WriteAllText(Path.Combine(appDir, "router-balancing.exe"), "app");

            var shadow = GitHubAppUpdateService.PrepareUpdaterShadowCopy(appDir);

            try
            {
                Assert.NotNull(shadow);
                Assert.True(File.Exists(Path.Combine(shadow, "RouterBalancing.Updater.exe")));
                Assert.True(File.Exists(Path.Combine(shadow, "RouterBalancing.Updater.dll")));
                Assert.True(File.Exists(Path.Combine(shadow, "RouterBalancing.Updater.deps.json")));
                Assert.True(File.Exists(Path.Combine(shadow, "RouterBalancing.Updater.runtimeconfig.json")));
                Assert.False(File.Exists(Path.Combine(shadow, "RouterBalancing.Updater.pdb")));
                Assert.False(File.Exists(Path.Combine(shadow, "router-balancing.exe")));
            }
            finally
            {
                if (shadow is not null)
                {
                    Directory.Delete(shadow, recursive: true);
                }
            }
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    [Fact]
    public void PrepareUpdaterShadowCopy_ReturnsNull_WhenUpdaterFilesMissing()
    {
        var appDir = Path.Combine(Path.GetTempPath(), "rb-updater-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDir);
        try
        {
            File.WriteAllText(Path.Combine(appDir, "router-balancing.exe"), "app");

            Assert.Null(GitHubAppUpdateService.PrepareUpdaterShadowCopy(appDir));
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_FailureAfterSuccess_ClearsPathAndDeletesPartialFile()
    {
        // 30 bytes / chunk 10: attempt 1 tải trọn zip + sig; attempt 2 ghi zip rồi ném IOException giữa chừng
        var payload = "0123456789ABCDEFGHIJ0123456789"u8.ToArray();
        using var key = new TestSigningKey();
        var release = SignedRelease(payload, key, out var signatureBase64);
        var service = ServiceWith(new GoodThenFailMidstreamHandler(payload, signatureBase64, chunkSize: 10, succeedBeforeFail: 2),
            publicKeyBase64: key.PublicKeyBase64);

        var firstPath = await service.DownloadUpdateAsync(release, null);

        Assert.NotNull(firstPath);
        Assert.True(File.Exists(firstPath));
        Assert.Equal(firstPath, service.DownloadedZipPath);

        var secondResult = await service.DownloadUpdateAsync(release, null);

        try
        {
            // Attempt 2 fail giữa chừng: path không được trỏ tới zip truncated, file rác phải bị xóa
            Assert.Null(secondResult);
            Assert.Null(service.DownloadedZipPath);
            Assert.False(File.Exists(firstPath));
        }
        finally
        {
            if (firstPath is not null)
            {
                File.Delete(firstPath);
            }
            if (service.DownloadedSignaturePath is not null)
            {
                File.Delete(service.DownloadedSignaturePath);
            }
        }
    }

    /// <summary>
    /// Release có cả zip lẫn file .sig cạnh nhau (đúng cấu trúc release thật);
    /// chữ ký được tạo bởi <paramref name="key"/> — test nào cần sai key tự ký bằng keypair khác.
    /// </summary>
    private static ReleaseInfo SignedRelease(byte[] payload, TestSigningKey key, out string signatureBase64)
    {
        signatureBase64 = key.SignToBase64(payload);
        return Release("v1.2.3",
            new ReleaseAsset(ZipAssetName, "https://example.com/payload.zip"),
            new ReleaseAsset(ZipAssetName + ".sig", "https://example.com/payload.zip.sig"));
    }

    // ===== HTTP doubles (pattern FreeModelSyncServiceTests) =====

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }

    /// <summary>
    /// Route theo URL: request .sig trả chữ ký Base64, còn lại trả payload zip theo chunk
    /// (để test progress nhiều lần thay vì 1 lần đọc trọn vẹn).
    /// <paramref name="signatureBase64"/> null mô phỏng release không có file chữ ký (404).
    /// </summary>
    private sealed class SignedZipHandler(byte[] payload, string? signatureBase64, int chunkSize = 81920)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsSignatureRequest(request))
            {
                return Task.FromResult(signatureBase64 is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signatureBase64) });
            }

            var content = new StreamContent(new ChunkedStream(payload, chunkSize));
            content.Headers.ContentLength = payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static bool IsSignatureRequest(HttpRequestMessage request) =>
        request.RequestUri is { AbsolutePath: var path } &&
        path.EndsWith(".sig", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lần tải zip đầu trả payload bình thường; các lần tải zip sau trả stream ném lỗi giữa chừng
    /// (mất mạng). Chữ ký .sig luôn trả bình thường.
    /// </summary>
    private sealed class GoodThenFailMidstreamHandler(byte[] payload, string? signatureBase64, int chunkSize,
        int succeedBeforeFail) : HttpMessageHandler
    {
        private int _zipCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsSignatureRequest(request))
            {
                return Task.FromResult(signatureBase64 is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signatureBase64) });
            }

            _zipCalls++;
            Stream stream = _zipCalls == 1
                ? new ChunkedStream(payload, chunkSize)
                : new FailAfterChunksStream(payload, chunkSize, succeedBeforeFail);
            var content = new StreamContent(stream);
            content.Headers.ContentLength = payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>Trả <paramref name="succeedBeforeFail"/> chunk đầu rồi ném IOException — mô phỏng mất mạng giữa chừng.</summary>
    private sealed class FailAfterChunksStream(byte[] data, int chunkSize, int succeedBeforeFail) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        private int _reads;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            MaybeFail();
            return _inner.Read(buffer, offset, Math.Min(count, chunkSize));
        }

        public override int Read(Span<byte> buffer)
        {
            MaybeFail();
            return _inner.Read(buffer[..Math.Min(buffer.Length, chunkSize)]);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            MaybeFail();
            return _inner.ReadAsync(buffer, offset, Math.Min(count, chunkSize), ct);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            MaybeFail();
            return _inner.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], ct);
        }

        private void MaybeFail()
        {
            if (_reads++ >= succeedBeforeFail)
            {
                throw new IOException("Simulated connection reset mid-download.");
            }
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, Math.Min(count, chunkSize));

        public override int Read(Span<byte> buffer) =>
            _inner.Read(buffer[..Math.Min(buffer.Length, chunkSize)]);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            _inner.ReadAsync(buffer, offset, Math.Min(count, chunkSize), ct);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            _inner.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], ct);

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingProgress : IProgress<int>
    {
        public List<int> Reports { get; } = [];

        public void Report(int value) => Reports.Add(value);
    }
}
