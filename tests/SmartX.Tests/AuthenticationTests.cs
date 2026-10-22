using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartX.Api.Security;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>Collects every log line a host writes, so a test can assert what was never logged.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<(LogLevel Level, string Message)> _lines = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._lines)
            {
                owner._lines.Add((logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception)));
            }
        }
    }
}

/// <summary>
/// Logging in: what it returns, what it refuses, and what slows a guesser down.
/// </summary>
public class LoginTests
{
    private const string OperatorPassword = AuthDefaults.DemoOperatorPassword;

    private static WebApplicationFactory<Program> Host(Dictionary<string, string?>? settings = null, FakeTimeProvider? clock = null) =>
        new AuthenticatedFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings ?? []));

            if (clock is not null)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(clock);
                });
            }
        });

    private static Task<HttpResponseMessage> LoginAsync(HttpClient http, string username, string password) =>
        http.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password));

    [Theory]
    [InlineData("operator", OperatorPassword, "Operator")]
    [InlineData("viewer", "smartx-viewer-demo", "Viewer")]
    public async Task The_right_password_returns_a_token_the_role_and_an_expiry_and_the_token_works(string username, string password, string role)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));
        using var api = Host(clock: clock);
        var http = api.Anonymous();

        var response = await LoginAsync(http, username, password);
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(role, login!.Role);
        Assert.Equal(username, login.Username);
        Assert.Equal(clock.GetUtcNow().AddMinutes(60), login.ExpiresAt);

        http.DefaultRequestHeaders.Authorization = TestAuth.Bearer(login.Token);
        var me = await http.GetFromJsonAsync<CurrentUser>("/api/auth/me");

        Assert.Equal(username, me!.Username);
        Assert.Equal(role, me.Role);
        Assert.Equal(login.ExpiresAt, me.ExpiresAt);

        // The lifetime is an option, not a constant. Checked once, on the first row, because each row starts its own host.
        if (role == "Operator")
        {
            using var short5 = Host(new() { ["Auth:TokenLifetime"] = "00:05:00" }, clock);
            var shortLogin = await (await LoginAsync(short5.Anonymous(), "operator", OperatorPassword)).Content.ReadFromJsonAsync<LoginResponse>();

            Assert.Equal(clock.GetUtcNow().AddMinutes(5), shortLogin!.ExpiresAt);
        }
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_user_get_exactly_the_same_answer_and_both_lock_the_same_way()
    {
        using var api = Host(new() { ["Auth:MaxFailedAttempts"] = "2" });
        var http = api.Anonymous();

        var wrongPassword = await LoginAsync(http, "operator", "not-the-password");
        var unknownUser = await LoginAsync(http, "nobody-by-this-name", "not-the-password");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(wrongPassword.StatusCode, unknownUser.StatusCode);

        var first = await wrongPassword.Content.ReadAsStringAsync();
        var second = await unknownUser.Content.ReadAsStringAsync();

        // Nothing in the answer, down to the wording, says which of the two it was.
        Assert.Equal(Normalise(first), Normalise(second));
        Assert.Contains("Incorrect username or password.", first, StringComparison.Ordinal);

        // And a name that does not exist is locked after the same number of failures as one that does.
        await LoginAsync(http, "ghost", "wrong");
        await LoginAsync(http, "ghost", "wrong");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(http, "ghost", "wrong")).StatusCode);
    }

    private static string Normalise(string problem)
    {
        // The one thing that legitimately differs is the trace id, which is per request.
        using var document = JsonDocument.Parse(problem);

        return string.Join("|", document.RootElement.EnumerateObject().Where(p => p.Name != "traceId").Select(p => p.Name + "=" + p.Value));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("operator", "")]
    [InlineData("  ", "x")]
    public async Task A_login_with_a_field_missing_is_a_400_and_not_a_guess(string username, string password)
    {
        using var api = Host();

        var response = await LoginAsync(api.Anonymous(), username, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_even_against_the_right_password_until_the_lockout_passes()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));
        using var api = Host(new() { ["Auth:MaxFailedAttempts"] = "3", ["Auth:LockoutDuration"] = "00:02:00" }, clock);
        var http = api.Anonymous();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(http, "operator", "wrong")).StatusCode);
        }

        var locked = await LoginAsync(http, "operator", OperatorPassword);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.True(locked.Headers.RetryAfter?.Delta > TimeSpan.Zero);

        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(http, "operator", OperatorPassword)).StatusCode);
    }

    [Fact]
    public async Task A_good_login_clears_the_count_of_failures()
    {
        using var api = Host(new() { ["Auth:MaxFailedAttempts"] = "3" });
        var http = api.Anonymous();

        await LoginAsync(http, "operator", "wrong");
        await LoginAsync(http, "operator", "wrong");
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(http, "operator", OperatorPassword)).StatusCode);

        await LoginAsync(http, "operator", "wrong");
        await LoginAsync(http, "operator", "wrong");

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(http, "operator", OperatorPassword)).StatusCode);
    }

    [Fact]
    public async Task The_login_endpoint_is_rate_limited_per_client_whatever_the_username()
    {
        using var api = Host(new() { ["Auth:LoginPermitLimit"] = "3", ["Auth:MaxFailedAttempts"] = "1000" });
        var http = api.Anonymous();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(http, $"guess-{attempt}", "wrong")).StatusCode);
        }

        var limited = await LoginAsync(http, "guess-4", "wrong");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task A_password_is_never_logged_whether_it_was_right_or_wrong()
    {
        var logs = new CapturingLoggerProvider();
        const string wrong = "Wr0ng-Probe-Value-77";

        using var api = new AuthenticatedFactory().WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace)));
        var http = api.Anonymous();

        await LoginAsync(http, "operator", wrong);
        await LoginAsync(http, "operator", OperatorPassword);
        await LoginAsync(http, "unknown", wrong);

        var everything = string.Join("\n", logs.Lines.Select(line => line.Message));

        Assert.DoesNotContain(wrong, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(OperatorPassword, everything, StringComparison.Ordinal);
        Assert.Contains("logged in as Operator", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void Passwords_are_stored_as_hashes_and_not_as_text()
    {
        var users = new UserDirectory(Options.Create(new AuthOptions { SeedDemoUsers = true }), new FakeEnvironment("Production"));

        foreach (var user in users.All)
        {
            Assert.StartsWith("AQAAAA", user.PasswordHash, StringComparison.Ordinal);
            Assert.DoesNotContain("demo", user.PasswordHash, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class FakeEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "SmartX.Api";

        public string ContentRootPath { get; set; } = "";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

/// <summary>
/// A token is only as good as the checks on it. Each of these is a token that must be refused, made wrong in one way.
/// </summary>
public class TokenValidationTests
{
    private static string Signed(
        string key = AuthDefaults.DevelopmentSigningKey,
        string issuer = "smartx-api",
        string audience = "smartx-operators",
        DateTime? expires = null,
        string role = UserRoles.Operator,
        string algorithm = SecurityAlgorithms.HmacSha256)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(30),
            Claims = new Dictionary<string, object> { ["name"] = "forger", ["role"] = role },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), algorithm),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static async Task<HttpStatusCode> ReadWithAsync(HttpClient http, string token)
    {
        // The token goes on this request alone, so one host can be asked about many tokens.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = TestAuth.Bearer(token);

        return (await http.SendAsync(request)).StatusCode;
    }

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Pad(string base64Url)
    {
        var value = base64Url.Replace('-', '+').Replace('_', '/');

        return value.PadRight(value.Length + ((4 - (value.Length % 4)) % 4), '=');
    }

    [Fact]
    public async Task A_good_token_is_accepted_which_is_what_makes_the_refusals_below_mean_something()
    {
        using var api = new AuthenticatedFactory();

        Assert.Equal(HttpStatusCode.OK, await ReadWithAsync(api.Anonymous(), Signed()));
    }

    /// <summary>
    /// Every way of forging or damaging a token, each of which must be a 401. One host and a loop: these used to be eleven
    /// tests, each starting its own host to ask one question. The message names the failing case.
    /// </summary>
    [Fact]
    public async Task A_token_that_is_expired_forged_or_damaged_in_any_way_is_refused()
    {
        using var api = new AuthenticatedFactory();
        var http = api.Anonymous();

        var exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var unsigned = Base64Url("""{"alg":"none","typ":"JWT"}""") + "." +
            Base64Url($$"""{"iss":"smartx-api","aud":"smartx-operators","name":"forger","role":"Operator","exp":{{exp}}}""") + ".";

        var signature = Signed(role: UserRoles.Viewer);
        var altered = signature[..^1] + (signature[^1] == 'A' ? 'B' : 'A');

        var parts = Signed(role: UserRoles.Viewer).Split('.');
        var promoted = Base64Url(Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[1]))).Replace("Viewer", "Operator", StringComparison.Ordinal));

        (string Case, string Token)[] cases =
        [
            ("expired five minutes ago", Signed(expires: DateTime.UtcNow.AddMinutes(-5))),
            ("made by the API an hour ago with a one hour life", TestAuth.Token(time: new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-61)))),
            ("signed with another key", Signed(key: "some-other-key-that-is-long-enough-to-sign-with")),
            ("another issuer", Signed(issuer: "someone-else")),
            ("another audience", Signed(audience: "someone-else")),
            ("a different algorithm", Signed(algorithm: SecurityAlgorithms.HmacSha384)),
            ("unsigned, algorithm none", unsigned),
            ("signature altered", altered),
            ("a viewer's payload edited to say operator", $"{parts[0]}.{promoted}.{parts[2]}"),
            ("garbage with three parts", "not.a.token"),
            ("garbage", "x"),
        ];

        foreach (var (name, token) in cases)
        {
            var status = await ReadWithAsync(http, token);

            Assert.True(status == HttpStatusCode.Unauthorized, $"{name}: expected 401, got {(int)status}.");
        }
    }

    [Fact]
    public async Task A_token_in_the_query_string_is_not_accepted_anywhere_but_the_hub()
    {
        using var api = new AuthenticatedFactory();
        var http = api.Anonymous();

        var response = await http.GetAsync($"/api/trust?access_token={TestAuth.Token()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

/// <summary>
/// The configuration that decides whether the API is protected: it must refuse to start without it, and say so when it is using the
/// published development values.
/// </summary>
public class AuthConfigurationTests
{
    private const string AttachmentKey = "jk8ISNL3b2INdPgIONnlaaf0nycILpHni4khrvGdBlg=";

    private static WebApplicationFactory<Program> Production(Dictionary<string, string?> settings, ILoggerProvider? logs = null) =>
        new AuthenticatedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Attachments:EncryptionKey"] = AttachmentKey }.Concat(settings).ToDictionary()));

            if (logs is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
            }
        });

    [Fact]
    public void Outside_development_the_api_will_not_start_without_a_signing_key_and_says_how_to_make_one()
    {
        using var api = Production(new() { ["Auth:FleetSecret"] = "a-fleet-secret-that-is-long-enough", ["Auth:SeedDemoUsers"] = "true" });

        var failure = Assert.Throws<OptionsValidationException>(() => api.CreateClient());

        Assert.Contains("Auth:SigningKey is required", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Outside_development_the_api_will_not_start_without_a_fleet_secret()
    {
        using var api = Production(new() { ["Auth:SigningKey"] = "a-signing-key-that-is-comfortably-long-enough", ["Auth:SeedDemoUsers"] = "true" });

        var failure = Assert.Throws<OptionsValidationException>(() => api.CreateClient());

        Assert.Contains("Auth:FleetSecret is required", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_signing_key_that_is_too_short_is_refused()
    {
        using var api = Production(new()
        {
            ["Auth:SigningKey"] = "too-short",
            ["Auth:FleetSecret"] = "a-fleet-secret-that-is-long-enough",
            ["Auth:SeedDemoUsers"] = "true",
        });

        var failure = Assert.Throws<OptionsValidationException>(() => api.CreateClient());

        Assert.Contains("at least 32 bytes", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_users_at_all_the_api_will_not_start_because_nobody_could_log_in()
    {
        using var api = Production(new()
        {
            ["Auth:SigningKey"] = "a-signing-key-that-is-comfortably-long-enough",
            ["Auth:FleetSecret"] = "a-fleet-secret-that-is-long-enough",
        });

        var failure = Assert.Throws<OptionsValidationException>(() => api.CreateClient());

        Assert.Contains("no users", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Demo_users_do_not_exist_outside_development_unless_asked_for()
    {
        var users = new UserDirectory(Options.Create(new AuthOptions()), new LoginTests_Environment("Production"));

        Assert.Empty(users.All);
    }

    [Fact]
    public async Task The_published_development_values_are_warned_about_outside_development()
    {
        var logs = new CapturingLoggerProvider();

        using var api = Production(
            new()
            {
                ["Auth:SigningKey"] = AuthDefaults.DevelopmentSigningKey,
                ["Auth:FleetSecret"] = AuthDefaults.DevelopmentFleetSecret,
                ["Auth:SeedDemoUsers"] = "true",
            },
            logs);

        _ = api.CreateClient();
        await Task.Delay(100);

        var warnings = string.Join("\n", logs.Lines.Where(line => line.Level == LogLevel.Warning).Select(line => line.Message));

        Assert.Contains("signed with the development key", warnings, StringComparison.Ordinal);
        Assert.Contains("development fleet secret", warnings, StringComparison.Ordinal);
        Assert.Contains("The user operator has the demo password", warnings, StringComparison.Ordinal);
        Assert.Contains("The user viewer has the demo password", warnings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Real_secrets_and_real_users_start_cleanly_with_no_warning_about_them()
    {
        var logs = new CapturingLoggerProvider();
        var hash = new Microsoft.AspNetCore.Identity.PasswordHasher<string>().HashPassword("u", "a-real-password-nobody-published");

        using var api = Production(
            new()
            {
                ["Auth:SigningKey"] = "a-signing-key-that-is-comfortably-long-enough",
                ["Auth:FleetSecret"] = "a-fleet-secret-that-is-long-enough",
                ["Auth:Users:0:Username"] = "alice",
                ["Auth:Users:0:PasswordHash"] = hash,
                ["Auth:Users:0:Role"] = "Operator",
            },
            logs);

        _ = api.CreateClient();
        await Task.Delay(100);

        var warnings = string.Join("\n", logs.Lines.Where(line => line.Level == LogLevel.Warning).Select(line => line.Message));

        Assert.DoesNotContain("published in the README", warnings, StringComparison.Ordinal);

        var login = await api.Anonymous().PostAsJsonAsync("/api/auth/login", new LoginRequest("alice", "a-real-password-nobody-published"));

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private sealed class LoginTests_Environment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "SmartX.Api";

        public string ContentRootPath { get; set; } = "";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

/// <summary>The per-device key, written independently of the code that makes it.</summary>
public class DeviceKeyDerivationTests
{
    // Worked out with another implementation (Node's crypto): HMAC-SHA256 of the MAC under the secret, as unpadded base64url.
    private const string Secret = "fleet-secret-for-tests";

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:01", "hJycmqzfx_sbyIgowKVHrguULo-kiRXp5LsokrS7TAU")]
    [InlineData("AA:BB:CC:DD:EE:02", "XOjtXuF3-N8qtVIp_icJAGni08Ftt83104WABR-aSKI")]
    public void The_key_is_the_hmac_of_the_mac_under_the_fleet_secret(string mac, string expected)
    {
        Assert.Equal(expected, DeviceKeys.Derive(Secret, mac));
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:01")]
    [InlineData("AA-BB-CC-DD-EE-01")]
    public void However_the_mac_is_written_the_key_is_the_same(string mac)
    {
        Assert.Equal(DeviceKeys.Derive(Secret, "AA:BB:CC:DD:EE:01"), DeviceKeys.Derive(Secret, mac));
    }

    [Fact]
    public void Different_devices_and_different_secrets_get_different_keys()
    {
        Assert.NotEqual(DeviceKeys.Derive(Secret, "AA:BB:CC:DD:EE:01"), DeviceKeys.Derive(Secret, "AA:BB:CC:DD:EE:02"));
        Assert.NotEqual(DeviceKeys.Derive(Secret, "AA:BB:CC:DD:EE:01"), DeviceKeys.Derive("another-secret-entirely", "AA:BB:CC:DD:EE:01"));
    }

    [Fact]
    public void Verify_accepts_the_right_key_and_nothing_else()
    {
        var key = DeviceKeys.Derive(Secret, "AA:BB:CC:DD:EE:01");

        Assert.True(DeviceKeys.Verify(Secret, "AA:BB:CC:DD:EE:01", key));
        Assert.False(DeviceKeys.Verify(Secret, "AA:BB:CC:DD:EE:02", key));
        Assert.False(DeviceKeys.Verify(Secret, "AA:BB:CC:DD:EE:01", key + "x"));
        Assert.False(DeviceKeys.Verify(Secret, "AA:BB:CC:DD:EE:01", key[..^1]));
        Assert.False(DeviceKeys.Verify(Secret, "AA:BB:CC:DD:EE:01", ""));
        Assert.False(DeviceKeys.Verify(Secret, "AA:BB:CC:DD:EE:01", null));
    }

    [Fact]
    public void The_key_is_safe_in_a_header()
    {
        var key = DeviceKeys.Derive(Secret, "AA:BB:CC:DD:EE:01");

        Assert.Matches("^[A-Za-z0-9_-]+$", key);
    }
}

/// <summary>The lockout's bookkeeping, on a clock a test controls.</summary>
public class LoginThrottleTests
{
    private static (LoginThrottle Throttle, FakeTimeProvider Clock) Make(int max = 3, int lockoutSeconds = 60)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new AuthOptions { MaxFailedAttempts = max, LockoutDuration = TimeSpan.FromSeconds(lockoutSeconds) });

        return (new LoginThrottle(options, clock), clock);
    }

    [Fact]
    public void A_name_is_free_until_it_has_failed_enough_times()
    {
        var (throttle, _) = Make();

        throttle.RecordFailure("a");
        throttle.RecordFailure("a");

        Assert.Null(throttle.LockedFor("a"));

        throttle.RecordFailure("a");

        Assert.NotNull(throttle.LockedFor("a"));
    }

    [Fact]
    public void Names_are_counted_without_regard_to_case_and_separately_from_each_other()
    {
        var (throttle, _) = Make(max: 2);

        throttle.RecordFailure("Alice");
        throttle.RecordFailure("alice");

        Assert.NotNull(throttle.LockedFor("ALICE"));
        Assert.Null(throttle.LockedFor("bob"));
    }

    [Fact]
    public void The_lock_ends_on_time_and_starts_a_fresh_count()
    {
        var (throttle, clock) = Make(max: 2, lockoutSeconds: 30);
        throttle.RecordFailure("a");
        throttle.RecordFailure("a");

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.NotNull(throttle.LockedFor("a"));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(throttle.LockedFor("a"));

        throttle.RecordFailure("a");
        Assert.Null(throttle.LockedFor("a"));
    }

    [Fact]
    public void A_flood_of_made_up_names_does_not_grow_the_table_without_limit()
    {
        var (throttle, _) = Make(max: 5);

        for (var name = 0; name < 25_000; name++)
        {
            throttle.RecordFailure("name-" + name);
        }

        // Never locked and never an exception: past the bound, new names are simply not tracked.
        Assert.Null(throttle.LockedFor("name-24999"));
        Assert.Null(throttle.LockedFor("name-0"));
    }
}
