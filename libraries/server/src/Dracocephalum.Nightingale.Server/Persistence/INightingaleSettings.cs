using System.Diagnostics.CodeAnalysis;

namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// Marks a type as settings a store is initialized with and that are kept in the settings rows.
/// It has no members: what it says is that the type was written to be flattened into rows and
/// bound back from them, public settable properties of plain values, so the extension methods
/// that do so are offered for it and for nothing else.
/// </summary>
[SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "A marker is the point: it limits the settings extension methods to types written to be stored as rows.")]
public interface INightingaleSettings;
