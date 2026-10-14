using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// How a reset is stored: <c>$tb</c> as the negative of the revision the stream continues from,
/// <c>-6</c> for a stream reset at revision 5, which no caller can set, since the metadata refuses
/// a negative truncation. One number says both that the stream was reset and where it is
/// truncated: it reads as not found while the mark is beyond its last revision, and from the
/// append that reaches the mark on, the mark is a plain truncation, which is what the reference's
/// un-delete writes. The value never leaves the gateway as stored: the metadata call reports the
/// truncation and the reset apart, and the reference's adapter translates it for its clients.
/// </summary>
internal static class ResetMark
{
    /// <summary>The revision a reset stream continues from, or <see langword="null"/> when the metadata holds no reset.</summary>
    /// <param name="stored">The metadata as stored.</param>
    /// <returns>The revision, positive.</returns>
    public static long? Find(StreamMetadata stored) =>
        stored.TruncateBefore is { } truncateBefore && truncateBefore < 0 ? -truncateBefore : null;

    /// <summary>
    /// The body of a metadata event: the metadata with its truncation as given, which may be a
    /// reset's negative mark or none at all. Written through the metadata's own JSON, so the
    /// reserved keys keep the reference's order, with the truncation put in its place.
    /// </summary>
    /// <param name="metadata">The metadata, valid; its own truncation is replaced.</param>
    /// <param name="truncateBefore">The truncation to store.</param>
    /// <returns>The UTF-8 JSON.</returns>
    public static byte[] ToBody(StreamMetadata metadata, long? truncateBefore)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (truncateBefore is not { } value)
        {
            return (metadata with { TruncateBefore = null }).ToJsonBytes();
        }

        // A placeholder the metadata accepts, so the key is written in its place; then the value.
        var body = JsonNode.Parse((metadata with { TruncateBefore = 0 }).ToJsonBytes())!.AsObject();
        body[StreamMetadataKeys.TruncateBefore] = value;
        return JsonSerializer.SerializeToUtf8Bytes(body, NightingaleJson.Default);
    }
}
