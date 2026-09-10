using Microsoft.JSInterop;

namespace HomeBlaze.Host.TimeZones;

/// <summary>
/// Thin wrapper over the timezone JS module: detects the browser zone and writes the preference cookie.
/// </summary>
public sealed class TimeZoneInterop : IAsyncDisposable
{
    /// <summary>Cookie that stores the display timezone preference ("Automatic" or an IANA id).</summary>
    public const string CookieName = "homeblaze.timezone";

    private readonly IJSRuntime _jsRuntime;
    private IJSObjectReference? _module;

    public TimeZoneInterop(IJSRuntime jsRuntime) => _jsRuntime = jsRuntime;

    private async ValueTask<IJSObjectReference> GetModuleAsync() =>
        _module ??= await _jsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/timezone.js");

    /// <summary>Returns the browser's IANA zone id, or null if it cannot be read.</summary>
    public async ValueTask<string?> GetBrowserTimeZoneAsync()
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<string?>("getBrowserTimeZone");
    }

    /// <summary>Persists the preference for one year via a SameSite=Lax cookie.</summary>
    public async ValueTask SetPreferenceCookieAsync(string cookieValue)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("setCookie", CookieName, cookieValue, 365);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }
}
