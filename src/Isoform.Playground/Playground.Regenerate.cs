using System.Runtime.InteropServices.JavaScript;
using Isoform.Interactive;

namespace Isoform.Playground;

public static partial class Playground
{
    /// <summary>
    /// A part document, or a bundle of an assembly and its documents, with edits applied: result v2. <paramref name="snapGrid"/>
    /// snaps the cell lattice to the world (<see cref="ModelingSettings.SnapGrid"/>), for a drag's previews.
    /// </summary>
    [JSExport]
    public static string Regenerate(string document, string edits, double cellSize, double chordTolerance, string slot, bool snapGrid) =>
        ViewJson.Write(Session.Regenerate(document, edits, Settings(cellSize, chordTolerance, snapGrid, slot), slot));

    // The last regeneration's mesh (see MeshBuffers), read by the page straight into WebGL buffers.

    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<double> Positions() => Session.Buffers.Positions;

    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<int> Faces() => Session.Buffers.Faces;

    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<double> Lines() => Session.Buffers.Lines;

    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<int> LineEdges() => Session.Buffers.LineEdges;

    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<double> SmoothLines() => Session.Buffers.SmoothLines;

    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<int> SmoothLineEdges() => Session.Buffers.SmoothLineEdges;
}
