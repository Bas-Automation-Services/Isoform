using System.Runtime.InteropServices.JavaScript;
using Isoform.Interactive;

namespace Isoform.Playground;

/// <summary>Documents as text an agent edits: edits applied without regenerating, typed changes turned back into edits.</summary>
public static partial class Playground
{
    [JSExport]
    public static string Apply(string document, string edits) => ViewJson.Apply(document, edits);

    [JSExport]
    public static string Diff(string old, string @new) => ViewJson.Diff(old, @new);
}
