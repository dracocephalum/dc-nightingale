namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The rules of an append to several streams that hold whatever the backend: what a request may
/// carry, and when an expected state holds against a stream's revision. The service checks a
/// request with them before anything is written, so every problem comes back at once; a backend
/// checks them again, since it is called by more than the service.
/// </summary>
public static class StreamAppends
{
    /// <summary>Whether an expected state holds against a stream's last revision, -1 when it has no events.</summary>
    /// <param name="expected">What the caller asserted.</param>
    /// <param name="actual">The stream's last revision, or -1.</param>
    /// <returns>Whether it holds.</returns>
    public static bool Holds(StreamState expected, long actual) =>
        expected == StreamState.Any
        || (expected == StreamState.NoStream && actual < 0)
        || (expected == StreamState.StreamExists && actual >= 0)
        || (expected.HasRevision && expected.ToInt64() == actual);

    /// <summary>
    /// Every problem with a request, in the order the parts were given, each naming its part: no
    /// appends, a part without events, a stream written twice, metadata that is not valid or that
    /// resets, a check that asserts nothing, names a stream also written, or is given twice.
    /// </summary>
    /// <param name="appends">The appends.</param>
    /// <param name="checks">The checks.</param>
    /// <returns>The problems; empty when the request is valid.</returns>
    public static IReadOnlyList<string> FindProblems(IReadOnlyList<StreamAppend> appends, IReadOnlyList<StreamCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(appends);
        ArgumentNullException.ThrowIfNull(checks);
        var problems = new List<string>();
        if (appends.Count == 0)
        {
            problems.Add("An append to several streams writes at least one.");
        }

        var written = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < appends.Count; i++)
        {
            var append = appends[i];
            if (!written.Add(append.Written))
            {
                problems.Add($"Append {i} writes {append.Written} again; each stream is written once.");
            }

            switch (append)
            {
                case StreamAppend.OfEvents { Events.Count: 0 }:
                    problems.Add($"Append {i} to {append.Stream} carries no events.");
                    break;
                case StreamAppend.OfMetadata { Metadata.TruncateBefore: long.MaxValue }:
                    problems.Add($"Append {i} sets the metadata of {append.Stream} with {StreamMetadataKeys.TruncateBefore} at its largest value, which resets the stream; a reset is its own call.");
                    break;
                case StreamAppend.OfMetadata metadata:
                    try
                    {
                        metadata.Metadata.Validate();
                    }
                    catch (ArgumentException invalid)
                    {
                        problems.Add($"Append {i} sets the metadata of {append.Stream}: {invalid.Message}");
                    }

                    break;
            }
        }

        var checkedStreams = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < checks.Count; i++)
        {
            var check = checks[i];
            if (check.Expected == StreamState.Any)
            {
                problems.Add($"Check {i} on {check.Stream} expects any state, which asserts nothing.");
            }

            if (written.Contains(check.Stream))
            {
                problems.Add($"Check {i} names {check.Stream}, which is written; its append's expected state is its check.");
            }
            else if (!checkedStreams.Add(check.Stream))
            {
                problems.Add($"Check {i} checks {check.Stream} again; each stream is checked once.");
            }
        }

        return problems;
    }
}
