using System.Collections.Frozen;
using System.Reflection;

using Dracocephalum.Nightingale.Protocol.V1;
using Google.Protobuf.Reflection;

namespace Dracocephalum.Nightingale.Protocol;

/// <summary>
/// The reason strings a failed call carries, derived from the contract's <see cref="ErrorReason"/>
/// enum so the server and the client can never disagree with the proto: each is the value's
/// original name minus its <c>ERROR_REASON_</c> prefix, exactly as <c>errors.proto</c> states.
/// </summary>
public static class ErrorReasons
{
    /// <summary>The <c>domain</c> of every error the server raises.</summary>
    public const string Domain = "nightingale";

    private const string Prefix = "ERROR_REASON_";

    private static readonly FrozenDictionary<ErrorReason, string> Names = Enum.GetValues<ErrorReason>()
        .ToFrozenDictionary(reason => reason, OriginalNameWithoutPrefix);

    private static readonly FrozenDictionary<string, ErrorReason> Reasons = Names
        .ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    /// <summary>The wire name of a reason.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns>The name, such as <c>REVISION_CONFLICT</c>.</returns>
    public static string NameOf(ErrorReason reason) => Names[reason];

    /// <summary>Parses a wire name back to a reason.</summary>
    /// <param name="name">The name as received.</param>
    /// <returns>The reason, or <see cref="ErrorReason.Unspecified"/> for a name this build does not know.</returns>
    public static ErrorReason Parse(string? name) =>
        name is not null && Reasons.TryGetValue(name, out var reason) ? reason : ErrorReason.Unspecified;

    private static string OriginalNameWithoutPrefix(ErrorReason reason)
    {
        // protoc keeps the proto's value name on the generated member; the C# name is a
        // PascalCased derivative and would drift from the contract's stated rule.
        var member = typeof(ErrorReason).GetField(reason.ToString(), BindingFlags.Public | BindingFlags.Static);
        var original = member?.GetCustomAttribute<OriginalNameAttribute>()?.Name ?? reason.ToString();
        return original.StartsWith(Prefix, StringComparison.Ordinal) ? original[Prefix.Length..] : original;
    }
}
