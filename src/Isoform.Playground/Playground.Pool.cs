using System.Runtime.InteropServices.JavaScript;

namespace Isoform.Playground;

/// <summary>
/// A pool of workers: each meshes its <see cref="Share"/> of the regions, and the coordinating worker
/// <see cref="Import"/>s the others' before it regenerates.
/// </summary>
public static partial class Playground
{
    // Held until the next share: the page reads the MemoryView after the call returns.
    static byte[] _share = [];

    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<byte> Share(string document, string edits, double cellSize, double chordTolerance, bool snapGrid, int index, int count) =>
        _share = Session.Share(document, edits, Settings(cellSize, chordTolerance, snapGrid), index, count);

    [JSExport]
    public static void Import(byte[] regions) => Session.Import(regions);

    [JSExport]
    public static void Sweep() => Session.Sweep();
}
