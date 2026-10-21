using System.Runtime.InteropServices.JavaScript;
using Isoform.Interactive;

namespace Isoform.Playground;

public static partial class Playground
{
    /// <summary>STEP of a slot's last good regeneration (see <see cref="ExportJson.Step"/>).</summary>
    [JSExport]
    public static string Step(string slot, string name) => ExportJson.Step(Session[slot], name);

    /// <summary>DXF of a sketch of the document, or (sketch empty) of a flat face of a slot's last good regeneration.</summary>
    [JSExport]
    public static string Dxf(string slot, string document, string sketch, string face) => ExportJson.Dxf(Session[slot], document, sketch, face);
}
