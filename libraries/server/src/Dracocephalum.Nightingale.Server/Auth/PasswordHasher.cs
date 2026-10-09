using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dracocephalum.Nightingale.Server.Auth;

/// <summary>
/// Makes and checks password hashes. PBKDF2-HMAC-SHA256 over the NFKC-normalized password, with
/// a random salt per hash and, when the host configured one, a pepper HMAC'd in first, so that a
/// copy of the table verifies nothing without the host's secret. The hash is a PHC string,
/// <c>$pbkdf2-sha256$i=600000,p=1$&lt;salt&gt;$&lt;hash&gt;</c>, which carries its own parameters:
/// the iterations can be raised and the pepper rotated, and a hash made under older ones still
/// verifies and says it wants remaking.
/// </summary>
public sealed class PasswordHasher
{
    private const string Algorithm = "pbkdf2-sha256";
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private readonly NightingaleOptionsBase.AuthSettings _settings;
    private readonly string _dummy;

    /// <summary>Initializes a new instance of the <see cref="PasswordHasher"/> class.</summary>
    /// <param name="settings">The iterations, the pepper and its id.</param>
    public PasswordHasher(NightingaleOptionsBase.AuthSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        // Verified against when the name is unknown, so that a wrong name costs the caller the
        // same time as a wrong password and the names that exist cannot be told apart.
        _dummy = Hash(Guid.NewGuid().ToString("N"));
    }

    /// <summary>Gets a hash no password matches, to verify an unknown name against.</summary>
    public string Dummy => _dummy;

    /// <summary>Hashes a password under the current parameters.</summary>
    /// <param name="password">The password.</param>
    /// <returns>The PHC string.</returns>
    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt, _settings.Iterations, _settings.PepperId);
        return $"${Algorithm}$i={_settings.Iterations.ToString(CultureInfo.InvariantCulture)},p={_settings.PepperId.ToString(CultureInfo.InvariantCulture)}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Checks a password against a hash.</summary>
    /// <param name="password">The password as presented.</param>
    /// <param name="encoded">The PHC string.</param>
    /// <returns>Whether it matches, and whether the hash was made under older parameters and wants remaking.</returns>
    public (bool Matches, bool NeedsRehash) Verify(string password, string encoded)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(encoded);
        var parts = encoded.Split('$');
        if (parts.Length != 5 || parts[0].Length != 0 || parts[1] != Algorithm)
        {
            return (false, false);
        }

        var iterations = 0;
        var pepperId = 0;
        foreach (var parameter in parts[2].Split(','))
        {
            var pair = parameter.Split('=', 2);
            if (pair.Length != 2 || !int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return (false, false);
            }

            switch (pair[0])
            {
                case "i":
                    iterations = value;
                    break;
                case "p":
                    pepperId = value;
                    break;
                default:
                    return (false, false);
            }
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return (false, false);
        }

        if (iterations <= 0 || expected.Length != HashSize)
        {
            return (false, false);
        }

        var actual = Derive(password, salt, iterations, pepperId);
        var matches = CryptographicOperations.FixedTimeEquals(actual, expected);
        return (matches, matches && (iterations < _settings.Iterations || pepperId != _settings.PepperId));
    }

    private byte[] Derive(string password, byte[] salt, int iterations, int pepperId)
    {
        // The pepper is applied by id, so a hash made under a previous pepper still verifies
        // while the previous secret is known; only the current id gets the current secret here,
        // and a hash under any other verifies as a mismatch until it is remade.
        var normalized = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        var material = string.IsNullOrEmpty(_settings.Pepper) || pepperId != _settings.PepperId
            ? normalized
            : HMACSHA256.HashData(Encoding.UTF8.GetBytes(_settings.Pepper), normalized);
        return Rfc2898DeriveBytes.Pbkdf2(material, salt, iterations, HashAlgorithmName.SHA256, HashSize);
    }
}
