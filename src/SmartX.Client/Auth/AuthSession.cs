using System.Text.Json;
using Microsoft.JSInterop;
using SmartX.Shared;

namespace SmartX.Client.Auth;

/// <summary>Where the session is kept between page loads. The browser's sessionStorage in the app, a dictionary in a test.</summary>
public interface ISessionStorage
{
    /// <summary>The stored text, or null.</summary>
    Task<string?> GetAsync(string key);

    /// <summary>Stores text.</summary>
    Task SetAsync(string key, string value);

    /// <summary>Forgets a key.</summary>
    Task RemoveAsync(string key);
}

/// <summary><see cref="ISessionStorage"/> over the browser's <c>sessionStorage</c>.</summary>
/// <remarks>
/// <b>Why sessionStorage and not localStorage.</b> Both are readable by any script on the page, so neither protects a token from
/// cross-site scripting (an httpOnly cookie would, at the price of CSRF handling and a same-site deployment this app does not
/// assume). What differs is how long a leaked or forgotten token lives. <c>localStorage</c> outlives the tab and the browser,
/// is shared by every tab and stays on a shared computer until someone clears it. <c>sessionStorage</c> is per tab, survives a
/// refresh (so reloading the page does not log you out), and is gone when the tab closes. For a console that controls actuators,
/// "closing the tab ends the session" is the property wanted, and the token's own short lifetime is the second limit.
/// </remarks>
public sealed class BrowserSessionStorage(IJSRuntime js) : ISessionStorage
{
    public async Task<string?> GetAsync(string key)
    {
        try
        {
            return await js.InvokeAsync<string?>("sessionStorage.getItem", key);
        }
        catch (JSException)
        {
            // Storage blocked (a privacy setting, a sandboxed frame): the session simply does not survive a refresh.
            return null;
        }
    }

    public async Task SetAsync(string key, string value)
    {
        try
        {
            await js.InvokeVoidAsync("sessionStorage.setItem", key, value);
        }
        catch (JSException)
        {
        }
    }

    public async Task RemoveAsync(string key)
    {
        try
        {
            await js.InvokeVoidAsync("sessionStorage.removeItem", key);
        }
        catch (JSException)
        {
        }
    }
}

/// <summary>Why a session ended when the user did not ask for it.</summary>
public enum SessionEnd
{
    /// <summary>The token's lifetime ran out.</summary>
    Expired,

    /// <summary>The API refused the token (401): it was revoked, the API restarted with another key, or it was altered.</summary>
    Rejected,
}

/// <summary>Who is signed in.</summary>
/// <param name="Username">Their name, shown in the app bar.</param>
/// <param name="Role">Viewer or Operator.</param>
/// <param name="ExpiresAt">When the token stops being accepted.</param>
public sealed record AuthUser(string Username, string Role, DateTimeOffset ExpiresAt);

/// <summary>
/// The signed-in user and their token: held in memory, mirrored to sessionStorage so a refresh does not sign them out, and ended
/// when the token runs out.
/// </summary>
/// <remarks>
/// A singleton, not scoped. The HTTP pipeline that adds the token to requests is built by <c>IHttpClientFactory</c> in a scope of its
/// own, which would hand it a different instance of anything scoped. One tab is one application in WebAssembly, so a singleton is one
/// per tab all the same.
/// <para>
/// <b>The role here is for clarity, never for security.</b> It decides which buttons are disabled and what the app bar says. The API
/// checks the role in the token on every request, so a user who edits this object in the browser gets enabled buttons and a 403.
/// </para>
/// </remarks>
public sealed class AuthSession(ISessionStorage storage, TimeProvider time) : IDisposable
{
    private const string StorageKey = "smartx.session";

    private ITimer? _expiry;
    private bool _restored;

    /// <summary>The signed-in user, or null.</summary>
    public AuthUser? User { get; private set; }

    /// <summary>The bearer token, or null when nobody is signed in.</summary>
    public string? Token { get; private set; }

    /// <summary>Whether anyone is signed in.</summary>
    public bool IsAuthenticated => User is not null;

    /// <summary>Whether the signed-in user may change things. Viewers may not.</summary>
    public bool CanChange => User?.Role == UserRoles.Operator;

    /// <summary>Raised when someone signs in or out, for any reason.</summary>
    public event Action? Changed;

    /// <summary>Raised when the session ended without the user asking, so the app can say why and go to the login page.</summary>
    public event Action<SessionEnd>? Ended;

    /// <summary>Reads the stored session once, dropping it if it has already expired.</summary>
    public async Task RestoreAsync()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;

        if (User is not null)
        {
            return;
        }

        var json = await storage.GetAsync(StorageKey);

        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<LoginResponse>(json);

            if (stored is not null && stored.ExpiresAt > time.GetUtcNow() && !string.IsNullOrEmpty(stored.Token))
            {
                Adopt(stored);

                return;
            }
        }
        catch (JsonException)
        {
            // Something else wrote there. Treated as no session.
        }

        await storage.RemoveAsync(StorageKey);
    }

    /// <summary>Signs in with what a successful login returned.</summary>
    public async Task SignInAsync(LoginResponse login)
    {
        _restored = true;
        Adopt(login);

        await storage.SetAsync(StorageKey, JsonSerializer.Serialize(login));
    }

    /// <summary>Signs out because the user asked to. Clears the stored copy.</summary>
    public async Task SignOutAsync()
    {
        Clear();

        await storage.RemoveAsync(StorageKey);
    }

    /// <summary>Ends the session because it ran out or was refused, and says so. Safe to call many times: only the first counts.</summary>
    public async Task EndAsync(SessionEnd reason)
    {
        if (User is null)
        {
            return;
        }

        Clear();
        await storage.RemoveAsync(StorageKey);

        Ended?.Invoke(reason);
    }

    /// <summary>
    /// Sets the in-memory session, starts the expiry timer and announces it. Does not touch storage; <see cref="SignInAsync"/> does
    /// that, and a test uses this directly to start already signed in.
    /// </summary>
    public void Adopt(LoginResponse login)
    {
        Token = login.Token;
        User = new AuthUser(login.Username, login.Role, login.ExpiresAt);

        ScheduleExpiry();

        Changed?.Invoke();
    }

    // A timer cannot be set further ahead than about 49 days, and a token lifetime is a setting that someone may make long. So it
    // waits at most a day at a time and, each time it wakes, either ends the session (the token has run out) or waits again.
    private static readonly TimeSpan LongestWait = TimeSpan.FromDays(1);

    private void ScheduleExpiry()
    {
        _expiry?.Dispose();

        var remaining = User is { } user ? user.ExpiresAt - time.GetUtcNow() : TimeSpan.Zero;
        var wait = remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining < LongestWait ? remaining : LongestWait;

        _expiry = time.CreateTimer(_ => OnExpiryTimer(), null, wait, Timeout.InfiniteTimeSpan);
    }

    private void OnExpiryTimer()
    {
        if (User is { } user && time.GetUtcNow() < user.ExpiresAt)
        {
            ScheduleExpiry();

            return;
        }

        _ = EndAsync(SessionEnd.Expired);
    }

    private void Clear()
    {
        _expiry?.Dispose();
        _expiry = null;
        Token = null;
        User = null;

        Changed?.Invoke();
    }

    public void Dispose() => _expiry?.Dispose();
}
