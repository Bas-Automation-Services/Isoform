using System.Runtime.InteropServices.JavaScript;
using Isoform.Interactive;

namespace Isoform.Playground;

public static partial class Playground
{
    /// <summary>A sketch of the document solved, <c>drag</c> a <c>{point, to}</c> JSON or empty (see <see cref="SketchJson"/>).</summary>
    [JSExport]
    public static string Sketch(string document, string id, string drag) => SketchJson.Write(document, id, drag);

    /// <summary>A sketch of the document filleted or trimmed, <c>edit</c> a <c>{op, corner, radius}</c> or <c>{op, entity, x, y}</c> JSON.</summary>
    [JSExport]
    public static string SketchEdit(string document, string id, string edit) => SketchJson.WriteEdit(document, id, edit);

    /// <summary>The plane to sketch on a face of a slot's last good regeneration.</summary>
    [JSExport]
    public static string FaceFrame(string slot, string face) => SketchJson.FaceFrame(Session[slot], face);
}
