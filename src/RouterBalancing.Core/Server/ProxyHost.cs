using System.Net;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Server;

/// <inheritdoc cref="IProxyHost"/>
public sealed class ProxyHost : IProxyHost, IAsyncDisposable
{
    private readonly IAppSettingsService _settings;
    private readonly ILogService _log;
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;
    private readonly IClientKeyService _clientKeys;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;
    private bool _disposed;

    public int? Port { get; private set; }

    public bool IsRunning => _app is not null;

    public event Action? StateChanged;

    public ProxyHost(IAppSettingsService settings, ILogService log, IDbContextFactory<RouterBalancingDbContext> db,
        ISecretProtector protector, IClientKeyService clientKeys)
    {
        _settings = settings;
        _log = log;
        _db = db;
        _protector = protector;
        _clientKeys = clientKeys;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_app is not null) return;

            var preferred = _settings.Port;
            var port = PortSelector.FindAvailable(preferred);
            if (port != preferred)
            {
                _log.Warn($"Port {preferred} đang bị chiếm — chuyển sang port {port}.");
            }

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(_settings);
            builder.Services.AddSingleton(_log);
            builder.Services.AddSingleton(_db);
            builder.Services.AddSingleton(_clientKeys);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));

            ProxyApp.ConfigureServices(builder, _protector);
            var app = builder.Build();
            ProxyApp.ConfigurePipeline(app);

            // StartAsync có thể thất bại thật (port bị đánh cắp giữa FindAvailable và
            // Listen, hoặc ct hủy) — nếu không dispose thì service provider/socket rò rỉ
            // và lần thử sau kế thừa state hỏng. State chỉ cập nhật khi start thành công.
            try
            {
                await app.StartAsync(cancellationToken);
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }

            _app = app;
            Port = port;
            _log.Info($"Proxy server đang chạy tại http://127.0.0.1:{port}/");
            StateChanged?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_app is null) return;
            var app = _app;

            // Bắt lỗi StopAsync (vd. ct hủy) để DisposeAsync vẫn chạy: nếu bỏ qua dispose
            // thì socket giữ nguyên port, Port/IsRunning sai lệch với XML doc của IProxyHost
            // và lần StartAsync sau không bind được. State chỉ xóa sau khi dispose xong.
            Exception? stopFailure = null;
            try
            {
                await app.StopAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                stopFailure = ex;
            }

            await app.DisposeAsync();
            _app = null;
            Port = null;
            _log.Info("Proxy server đã dừng.");
            StateChanged?.Invoke();

            if (stopFailure is not null)
            {
                ExceptionDispatchInfo.Capture(stopFailure).Throw();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
