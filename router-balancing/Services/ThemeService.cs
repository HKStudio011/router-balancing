using Microsoft.JSInterop;
using RouterBalancing.Core.Settings;

namespace router_balancing.Services;

/// <summary>Áp theme từ setting lên DOM qua JS global rbTheme (xem vite-project/src/ts/main.ts).</summary>
public sealed class ThemeService(IAppSettingsService settings, IJSRuntime js)
{
    public async Task ApplyAsync() =>
        await js.InvokeVoidAsync("rbTheme.applyTheme", settings.Theme);
}
