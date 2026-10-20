using System.Runtime.InteropServices.JavaScript;
using Isoform.Interactive;

namespace Isoform.Playground;

public static partial class Playground
{
    /// <summary>The drag handles of a slot's last good regeneration: <c>{slot, hash, handles}</c>.</summary>
    [JSExport]
    public static string Handles(string slot) => HandleJson.Write(slot, Session[slot]);
}
