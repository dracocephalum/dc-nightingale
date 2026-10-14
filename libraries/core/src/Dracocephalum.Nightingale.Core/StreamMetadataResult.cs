namespace Dracocephalum.Nightingale;

/// <summary>A stream's metadata as last set, with the revision of the metadata stream that holds it.</summary>
/// <param name="Stream">The stream the metadata is for.</param>
/// <param name="Revision">The metadata stream's last revision, which the next set expects; -1 when nothing was set.</param>
/// <param name="Metadata">The metadata in force; <see cref="StreamMetadata.Empty"/> when nothing was set.</param>
public sealed record StreamMetadataResult(string Stream, long Revision, StreamMetadata Metadata)
{
    /// <summary>Gets a value indicating whether metadata was ever set on the stream.</summary>
    public bool Exists => Revision >= 0;
}
