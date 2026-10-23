using Isoform.Document;

namespace Isoform.Sketching;

/// <summary>
/// A sketch after an edit: the new sketch (settled, every constraint holding), the ids of the points and entities the
/// edit added, and the constraints it had to drop with a deleted piece, as <see cref="SketchSolver.Describe"/> words
/// them. An edit never drops a constraint without listing it.
/// </summary>
public sealed record SketchEdit(Sketch Sketch, IReadOnlyList<string> Added, IReadOnlyList<string> Dropped);

/// <summary>
/// Fillet and trim: edits of a sketch's lines, arcs and circles, shared by the page, the agent tools and MCP. The
/// result is still lines and arcs. An edit that cannot be made throws an <see cref="ArgumentException"/> saying why.
/// </summary>
public static partial class SketchEdits;
