using RouterBalancing.Core.Settings;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Áp dụng setting <c>providerProbeTimeoutSec</c> cho mỗi request của client provider-probe
/// (spec manual-retry §3.8) — đọc <see cref="IAppSettingsService"/> tại thời điểm gửi nên
/// đổi setting có hiệu lực ngay, không cần rebuild HttpClient.
/// </summary>
public sealed class ProviderProbeTimeoutHandler : DelegatingHandler
{
    private readonly IAppSettingsService _settings;

    public ProviderProbeTimeoutHandler(IAppSettingsService settings)
    {
        _settings = settings;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Linked CTS: timeout của handler KHÔNG được cancel token gốc của caller —
        // caller tự huỷ (TestConnection/FetchModels...) vẫn nhận OperationCanceledException bình thường.
        using var timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_settings.ProviderProbeTimeoutSec));
        try
        {
            return await base.SendAsync(request, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (
            timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Đổi thành TaskCanceledException có message timeout — caller code đang bắt
            // sẵn TaskCanceledException (xem ProviderEndpointMetadataProvider) không đổi hành vi.
            throw new TaskCanceledException(
                $"Provider probe exceeded {_settings.ProviderProbeTimeoutSec}s timeout.");
        }
    }
}
