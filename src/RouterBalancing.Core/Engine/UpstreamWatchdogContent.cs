using System.Net;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// HttpContent bọc response upstream, trả <see cref="IdleWatchdogStream"/> từ
/// <see cref="CreateContentReadStreamAsync"/> — single seam để mọi điểm đọc của protocol
/// (UsageCapture.TeeAsync, AggregateSseAsync các protocol) đều đi qua watchdog mà không sửa
/// IProxyProtocol signature. Copy header trong ctor: protocol đọc
/// <see cref="HttpContentHeaders.ContentType"/> để phân biệt SSE/JSON.
/// </summary>
internal sealed class UpstreamWatchdogContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly TimeSpan _idleTimeout;

    /// <summary>Bọc <paramref name="inner"/> bằng idle watchdog.</summary>
    /// <param name="inner">Response content gốc từ upstream.</param>
    /// <param name="idleTimeout">Ngưỡng idle-read; &gt; 0 (0 = tắt watchdog, không wrap).</param>
    public UpstreamWatchdogContent(HttpContent inner, TimeSpan idleTimeout)
    {
        _inner = inner;
        _idleTimeout = idleTimeout;
        foreach (var header in inner.Headers)
            Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    /// <inheritdoc/>
    protected override async Task<Stream> CreateContentReadStreamAsync()
    {
        var stream = await _inner.ReadAsStreamAsync();
        return new IdleWatchdogStream(stream, _idleTimeout);
    }

    /// <inheritdoc/>
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var innerStream = await _inner.ReadAsStreamAsync();
        await innerStream.CopyToAsync(stream);
    }

    /// <inheritdoc/>
    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
