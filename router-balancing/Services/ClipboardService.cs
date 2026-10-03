using Microsoft.JSInterop;

namespace router_balancing.Services;

/// <inheritdoc cref="IClipboardService"/>
public sealed class ClipboardService(IJSRuntime js) : IClipboardService
{
    public async Task<bool> CopyAsync(string text)
    {
        try
        {
            await js.InvokeVoidAsync("navigator.clipboard.writeText", text);
            return true;
        }
        catch (JSException)
        {
            // NotAllowedError khi document không focus — trả false thay vì văng exception (D-D2)
            return false;
        }
    }
}
