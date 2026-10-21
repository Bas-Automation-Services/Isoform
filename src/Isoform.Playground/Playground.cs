using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Isoform.Interactive;
using Isoform.Modeling;

namespace Isoform.Playground;

/// <summary>
/// What the page's workers call: thin <c>[JSExport]</c> wrappers over one <see cref="Session"/>, one partial file per
/// area. Strings in and out, meshes as MemoryViews; the JSON shapes are in <c>src/Isoform.Interactive/README.md</c>.
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class Playground
{
    static readonly Session Session = new();

    // snapGrid puts the lattice on a fixed world grid: a drag's previews keep one lattice (no shimmer) and the regions
    // an edit leaves alone stay in the cache, even when the part's bounding box moves. No local refinement: the page
    // reports what is below the resolution and offers the cell size that resolves it (lib/diagnose.js).
    static ModelingSettings Settings(double cellSize, double chordTolerance, bool snapGrid) =>
        new() { CellSize = cellSize, ChordTolerance = chordTolerance, SnapGrid = snapGrid, Refinement = 0 };

    /// <summary>The example documents, name → canonical text.</summary>
    [JSExport]
    public static string Examples() => ViewJson.Examples();
}
