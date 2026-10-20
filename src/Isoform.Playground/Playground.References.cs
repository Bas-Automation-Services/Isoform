using System.Runtime.InteropServices.JavaScript;
using Isoform.Interactive;

namespace Isoform.Playground;

/// <summary>Pinned faces and edges, resolved by name against a slot's last good result (see <see cref="ReferencePins.Json"/>).</summary>
public static partial class Playground
{
    [JSExport]
    public static string Resolve(string refs, string slot) => ReferencePins.Json(refs, Session[slot]);
}
