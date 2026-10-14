namespace Dracocephalum.Nightingale;

/// <summary>A stream's metadata as last set, with the revision of the metadata stream that holds it.</summary>
/// <param name="Stream">The stream the metadata is for.</param>
/// <param name="Revision">The metadata stream's last revision, which the next set expects; -1 when nothing was set.</param>
/// <param name="Metadata">The metadata in force; <see cref="StreamMetadata.Empty"/> when nothing was set. After a reset its <see cref="StreamMetadata.TruncateBefore"/> is the revision the stream continues from.</param>
/// <param name="Reset">Whether the stream is reset and not yet recreated: it reads as not found until its next append, or the next set of its metadata, brings it back from the truncation on.</param>
public sealed record StreamMetadataResult(string Stream, long Revision, StreamMetadata Metadata, bool Reset = false)
{
    /// <summary>Gets a value indicating whether metadata was ever set on the stream.</summary>
    public bool Exists => Revision >= 0;
}
