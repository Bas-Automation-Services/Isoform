using System.Runtime.InteropServices.JavaScript;
using Isoform.Interactive;

namespace Isoform.Playground;

public static partial class Playground
{
    /// <summary>A sketch of the document solved, <c>drag</c> a <c>{point, to}</c> JSON or empty (see <see cref="SketchJson"/>).</summary>
    [JSExport]
    public static string Sketch(string document, string id, string drag) => SketchJson.Write(document, id, drag);

    /// <summary>The plane to sketch on a face of a slot's last good regeneration.</summary>
    [JSExport]
    public static string FaceFrame(string slot, string face) => SketchJson.FaceFrame(Session[slot], face);
}
