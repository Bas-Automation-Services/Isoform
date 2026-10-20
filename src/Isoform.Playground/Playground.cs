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

    static ModelingSettings Settings(double cellSize, double chordTolerance) => new() { CellSize = cellSize, ChordTolerance = chordTolerance };

    /// <summary>The example documents, name → canonical text.</summary>
    [JSExport]
    public static string Examples() => ViewJson.Examples();
}
