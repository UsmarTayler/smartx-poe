using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SmartX.Shared;

namespace SmartX.Api.Security;

/// <summary>A user the gateway knows: a name, a role and a password hash. Never a password.</summary>
internal sealed record UserAccount(string Username, string PasswordHash, string Role);

/// <summary>
/// The users, in memory like the rest of the gateway, seeded from configuration.
/// </summary>
/// <remarks>
/// Looked up by name without regard to case. A user store behind this class (a database, an identity provider) replaces it and
/// nothing that logs in, because the login path only asks for an account and a hash to check.
/// </remarks>
internal sealed class UserDirectory
{
    // Hashes of the published demo passwords (see AuthDefaults and the README). A hash and not a password, so nothing in the
    // source is a password a person could type; the demo passwords are documented, like the development encryption key.
    private const string DemoOperatorHash = "AQAAAAIAAYagAAAAEE8SNjz3t3vAMY83OQ6Xyo1rEEtUc7vHajd+J+wnkM5FSSSJRpR3GFgNT04er8TBig==";
    private const string DemoViewerHash = "AQAAAAIAAYagAAAAEIjJEoaTv/0NZqld+qAowKlHBPzWPZA0MSV1nhub/Tl2B7px81UcYf+u8kHPalelAg==";

    private readonly Dictionary<string, UserAccount> _users = new(StringComparer.OrdinalIgnoreCase);

    public UserDirectory(IOptions<AuthOptions> options, IHostEnvironment environment)
    {
        var settings = options.Value;

        if (settings.SeedDemoUsers ?? environment.IsDevelopment())
        {
            Add(new UserAccount("operator", DemoOperatorHash, UserRoles.Operator));
            Add(new UserAccount("viewer", DemoViewerHash, UserRoles.Viewer));
        }

        foreach (var user in settings.Users)
        {
            Add(new UserAccount(user.Username.Trim(), user.PasswordHash, user.Role));
        }
    }

    /// <summary>Every account, for the start-up check.</summary>
    public IReadOnlyCollection<UserAccount> All => _users.Values;

    /// <summary>The account with this name, or null.</summary>
    public UserAccount? Find(string username) => _users.GetValueOrDefault(username.Trim());

    private void Add(UserAccount account) => _users[account.Username] = account;
}

/// <summary>Checks a password against a hash, and takes as long to say no to a name it does not know as to a wrong password.</summary>
/// <remarks>
/// Hashing is deliberately slow (100,000 PBKDF2 rounds). If an unknown username skipped it, the difference in response time would
/// tell an attacker which usernames exist, even though the message is the same. So an unknown name is checked against a hash of
/// no one's password, and the answer is false.
/// </remarks>
internal sealed class PasswordVerifier
{
    // The hash of a password nobody has, so an unknown username costs the same as a known one.
    private const string NobodyHash = "AQAAAAIAAYagAAAAEPBclHMFmHmnMHUOAr9XAnVmw+8LICN/ugO3EFaJP64JlEOHiQsSyS7baBLFOvZt+A==";

    private readonly PasswordHasher<UserAccount> _hasher = new();

    public bool Verify(UserAccount? account, string password)
    {
        var hash = account?.PasswordHash ?? NobodyHash;
        var result = _hasher.VerifyHashedPassword(account ?? new UserAccount("", "", ""), hash, password);

        return account is not null && result != PasswordVerificationResult.Failed;
    }

    /// <summary>Is the account's password one of the published demo passwords? Used only to warn when one is in use outside Development.</summary>
    public bool UsesPublishedDemoPassword(UserAccount account) =>
        Verify(account, AuthDefaults.DemoOperatorPassword) || Verify(account, AuthDefaults.DemoViewerPassword);
}
