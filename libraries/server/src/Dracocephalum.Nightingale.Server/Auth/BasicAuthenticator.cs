using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>
/// Authenticates the Basic scheme: a user name and password, checked against the built-in
/// administrator from the host's configuration first and the credentials table second. A name
/// and password that verified are remembered for a while, keyed by the name and a digest of the
/// password, so the slow hash is not recomputed on every call; the memory is dropped when the
/// credential's security stamp changes. Wrong passwords are counted on the row, and a credential
/// over the threshold is refused for the lockout duration; the administrator's count is kept
/// here, per instance. Every refusal is the same refusal: which part was wrong is not said.
/// </summary>
public sealed partial class BasicAuthenticator(NightingaleOptionsBase.AuthSettings settings, PasswordHasher hasher, ICredentialStore credentials, TimeProvider time, ILogger<BasicAuthenticator> logger)
{
    /// <summary>The scheme, as it appears in the header.</summary>
    public const string Scheme = "Basic";

    private readonly ConcurrentDictionary<string, Verified> _verified = new(StringComparer.Ordinal);
    private readonly Lock _adminGate = new();
    private int _adminFailures;
    private DateTimeOffset? _adminLockedUntil;

    /// <summary>Authenticates the credentials after the scheme.</summary>
    /// <param name="parameter">The base64 text after <c>Basic</c>.</param>
    /// <param name="peer">Where the call came from, for the log.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The principal, or <see langword="null"/> when the credentials are refused.</returns>
    public async Task<NightingalePrincipal?> AuthenticateAsync(string parameter, string peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parameter.Trim()));
        }
        catch (FormatException)
        {
            return null;
        }

        var colon = decoded.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return null;
        }

        var name = decoded[..colon];
        var password = decoded[(colon + 1)..];
        var key = CacheKey(name, password);
        var now = time.GetUtcNow();
        if (_verified.TryGetValue(key, out var remembered) && remembered.ExpiresAt > now)
        {
            return remembered.Principal;
        }

        var principal = string.Equals(name, UserCredentials.AdminUserName, StringComparison.OrdinalIgnoreCase) && !settings.AdminDisabled
            ? AuthenticateAdmin(password, now)
            : await AuthenticateStoredAsync(name, password, now, cancellationToken).ConfigureAwait(false);
        if (principal is null)
        {
            LogRefused(logger, name, peer);
            return null;
        }

        if (settings.VerificationCacheDuration > TimeSpan.Zero)
        {
            _verified[key] = new Verified(principal, now + settings.VerificationCacheDuration);
        }

        return principal;
    }

    /// <summary>Forgets what was verified for a credential, because its row changed.</summary>
    /// <param name="name">The user name.</param>
    public void Forget(string name)
    {
        foreach (var key in _verified.Keys.Where(key => key.StartsWith(name + "\n", StringComparison.Ordinal)))
        {
            _verified.TryRemove(key, out _);
        }
    }

    private static string CacheKey(string name, string password) =>
        name + "\n" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    private NightingalePrincipal? AuthenticateAdmin(string password, DateTimeOffset now)
    {
        lock (_adminGate)
        {
            if (_adminLockedUntil is { } until && until > now)
            {
                return null;
            }

            // The configured password is compared in constant time, through the same derivation
            // a stored one goes through, so neither path is faster than the other.
            var expected = Encoding.UTF8.GetBytes((settings.AdminPassword ?? string.Empty).Normalize(NormalizationForm.FormKC));
            var presented = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
            if (expected.Length > 0 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(expected), SHA256.HashData(presented)))
            {
                _adminFailures = 0;
                _adminLockedUntil = null;
                return new NightingalePrincipal(UserCredentials.AdminUserName, CredentialRole.Admin, null, Scheme);
            }

            _adminFailures++;
            if (settings.LockoutThreshold > 0 && _adminFailures >= settings.LockoutThreshold)
            {
                _adminLockedUntil = now + settings.LockoutDuration;
                _adminFailures = 0;
            }

            return null;
        }
    }

    private async Task<NightingalePrincipal?> AuthenticateStoredAsync(string name, string password, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = await credentials.FindAsync(name, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            // Costs the same as a wrong password would.
            hasher.Verify(password, hasher.Dummy);
            return null;
        }

        if (row.IsDisabled || (row.LockedUntil is { } until && until > now))
        {
            hasher.Verify(password, hasher.Dummy);
            return null;
        }

        var (matches, needsRehash) = hasher.Verify(password, row.PasswordHash);
        if (!matches)
        {
            await credentials.RecordFailureAsync(row.Id, settings.LockoutThreshold, settings.LockoutDuration, cancellationToken).ConfigureAwait(false);
            return null;
        }

        await credentials.RecordSuccessAsync(row.Id, needsRehash ? hasher.Hash(password) : null, cancellationToken).ConfigureAwait(false);
        return new NightingalePrincipal(row.Name, row.Role, row.TenantId, Scheme);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Credentials refused for '{Name}' from {Peer}.")]
    private static partial void LogRefused(ILogger logger, string name, string peer);

    private sealed record Verified(NightingalePrincipal Principal, DateTimeOffset ExpiresAt);
}
