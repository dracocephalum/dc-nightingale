using System.Globalization;
using System.Net;

namespace Dracocephalum.Nightingale.Client;

/// <summary>
/// How a client reaches a Nightingale server, read from one connection string in the shape of the
/// reference client's: <c>nightingale://[user:password@]host[:port][,host[:port]...][?key=value&amp;...]</c>.
/// Everything a client needs is in that string, so an application keeps it where it keeps its
/// other connection strings and nothing else is configured. Several hosts are the instances of
/// one cluster, used in rotation; <c>nightingale+discover://</c> names one host whose DNS record
/// lists them. The keys, case-insensitive: <c>tls</c>, <c>tlsVerifyCert</c>,
/// <c>keepAliveInterval</c>, <c>keepAliveTimeout</c> and <c>defaultDeadline</c>, the last three in
/// milliseconds, and <c>tenant</c>, the tenant a global credential works in, a UUID or <c>*</c>.
/// A key the client does not know is refused, so a misspelt setting never passes for a default.
/// </summary>
public sealed record NightingaleClientSettings
{
    /// <summary>The scheme of a connection string that lists the instances.</summary>
    public const string Scheme = "nightingale";

    /// <summary>The scheme of a connection string that names one host whose DNS record lists the instances.</summary>
    public const string DiscoverScheme = "nightingale+discover";

    /// <summary>The port used when a host names none, the reference's.</summary>
    public const int DefaultPort = 2113;

    private static readonly TimeSpan DefaultKeepAlive = TimeSpan.FromSeconds(10);

    /// <summary>Gets the instances, or the one host to resolve when <see cref="Discover"/> is set.</summary>
    public required IReadOnlyList<DnsEndPoint> Endpoints { get; init; }

    /// <summary>Gets a value indicating whether the one host's DNS record lists the instances.</summary>
    public bool Discover { get; init; }

    /// <summary>Gets a value indicating whether the connection is encrypted; it is unless the string says <c>tls=false</c>.</summary>
    public bool Tls { get; init; } = true;

    /// <summary>Gets a value indicating whether the server's certificate is verified; <c>tlsVerifyCert=false</c> is for development only.</summary>
    public bool TlsVerifyCertificate { get; init; } = true;

    /// <summary>Gets how long a connection may be idle before it is pinged, or <see langword="null"/> for never (<c>keepAliveInterval=-1</c>).</summary>
    public TimeSpan? KeepAliveInterval { get; init; } = DefaultKeepAlive;

    /// <summary>Gets how long a ping may go unanswered before the connection is closed, or <see langword="null"/> for no limit (<c>keepAliveTimeout=-1</c>).</summary>
    public TimeSpan? KeepAliveTimeout { get; init; } = DefaultKeepAlive;

    /// <summary>Gets the deadline given to a call that is not a read or a subscription and carries none of its own, or <see langword="null"/> for none.</summary>
    public TimeSpan? DefaultDeadline { get; init; }

    /// <summary>Gets the user name in the string, if any; sent with every call, with the password, in the <c>authorization</c> header.</summary>
    public string? UserName { get; init; }

    /// <summary>Gets the password in the string, if any.</summary>
    public string? Password { get; init; }

    /// <summary>Gets the tenant sent with every call in the <c>nightingale-tenant</c> header, a UUID or <c>*</c>; <see langword="null"/> to send none, which a tenant-bound credential needs not.</summary>
    public string? Tenant { get; init; }

    /// <summary>Reads a connection string.</summary>
    /// <param name="connectionString">The connection string.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="FormatException">The string is not a Nightingale connection string; the message says which part, and never repeats the credentials.</exception>
    public static NightingaleClientSettings Parse(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var text = connectionString.Trim();

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            throw new FormatException($"A connection string starts with {Scheme}:// or {DiscoverScheme}://.");
        }

        var scheme = text[..schemeEnd];
        var discover = scheme.Equals(DiscoverScheme, StringComparison.OrdinalIgnoreCase);
        if (!discover && !scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"The scheme '{scheme}' is not {Scheme} or {DiscoverScheme}.");
        }

        var rest = text[(schemeEnd + 3)..];
        var queryStart = rest.IndexOf('?', StringComparison.Ordinal);
        var query = queryStart < 0 ? string.Empty : rest[(queryStart + 1)..];
        var authority = queryStart < 0 ? rest : rest[..queryStart];

        string? userName = null;
        string? password = null;
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            (userName, password) = ParseCredentials(authority[..at]);
            authority = authority[(at + 1)..];
        }

        if (authority.EndsWith('/'))
        {
            authority = authority[..^1];
        }

        if (authority.Contains('/', StringComparison.Ordinal))
        {
            throw new FormatException("A connection string has no path: after the hosts comes '?' and the settings, or nothing.");
        }

        var endpoints = authority.Split(',').Select(ParseEndpoint).ToList();
        if (discover && endpoints.Count != 1)
        {
            throw new FormatException($"{DiscoverScheme}:// names exactly one host, the one whose DNS record lists the instances.");
        }

        var settings = new NightingaleClientSettings
        {
            Endpoints = endpoints,
            Discover = discover,
            UserName = userName,
            Password = password,
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new FormatException($"The setting '{pair}' is not of the form key=value.");
            }

            var key = pair[..equals];
            if (!seen.Add(key))
            {
                throw new FormatException($"The setting '{key}' is given more than once.");
            }

            settings = Apply(settings, key, Uri.UnescapeDataString(pair[(equals + 1)..]));
        }

        return settings;
    }

    private static NightingaleClientSettings Apply(NightingaleClientSettings settings, string key, string value) =>
        key.ToUpperInvariant() switch
        {
            "TLS" => settings with { Tls = ParseBoolean(key, value) },
            "TLSVERIFYCERT" => settings with { TlsVerifyCertificate = ParseBoolean(key, value) },
            "KEEPALIVEINTERVAL" => settings with { KeepAliveInterval = ParseMilliseconds(key, value, allowNever: true) },
            "KEEPALIVETIMEOUT" => settings with { KeepAliveTimeout = ParseMilliseconds(key, value, allowNever: true) },
            "DEFAULTDEADLINE" => settings with { DefaultDeadline = ParseMilliseconds(key, value, allowNever: false) },
            "TENANT" => settings with { Tenant = ParseTenant(key, value) },
            _ => throw new FormatException($"The setting '{key}' is not known; the settings are tls, tlsVerifyCert, keepAliveInterval, keepAliveTimeout, defaultDeadline and tenant."),
        };

    private static string ParseTenant(string key, string value) =>
        value == "*" || Guid.TryParseExact(value, "D", out _)
            ? value
            : throw new FormatException($"The setting '{key}' must be a UUID in its canonical form or '*'.");

    private static (string UserName, string? Password) ParseCredentials(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var userName = Uri.UnescapeDataString(colon < 0 ? text : text[..colon]);
        if (userName.Length == 0)
        {
            throw new FormatException("The credentials before '@' name no user.");
        }

        return (userName, colon < 0 ? null : Uri.UnescapeDataString(text[(colon + 1)..]));
    }

    private static DnsEndPoint ParseEndpoint(string text)
    {
        // An IPv6 literal is bracketed, so its own colons are not taken for the port's.
        var bracketed = text.StartsWith('[');
        var closing = bracketed ? text.IndexOf(']', StringComparison.Ordinal) : -1;
        if (bracketed && closing < 0)
        {
            throw new FormatException($"The host '{text}' opens a bracket it does not close.");
        }

        var colon = text.IndexOf(':', closing + 1);
        var host = colon < 0 ? text : text[..colon];
        if (host.Length == 0 || Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown)
        {
            throw new FormatException($"'{text}' is not a host, or a host and a port.");
        }

        if (colon < 0)
        {
            return new DnsEndPoint(host, DefaultPort);
        }

        if (!int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            throw new FormatException($"The port of '{text}' is not a number from 1 to 65535.");
        }

        return new DnsEndPoint(host, port);
    }

    private static bool ParseBoolean(string key, string value) =>
        bool.TryParse(value, out var parsed)
            ? parsed
            : throw new FormatException($"The setting '{key}' is true or false, not '{value}'.");

    private static TimeSpan? ParseMilliseconds(string key, string value, bool allowNever)
    {
        if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var milliseconds)
            || milliseconds == 0
            || milliseconds < -1
            || (milliseconds == -1 && !allowNever))
        {
            throw new FormatException(allowNever
                ? $"The setting '{key}' is a number of milliseconds above zero, or -1 for never, not '{value}'."
                : $"The setting '{key}' is a number of milliseconds above zero, not '{value}'.");
        }

        return milliseconds == -1 ? null : TimeSpan.FromMilliseconds(milliseconds);
    }
}
