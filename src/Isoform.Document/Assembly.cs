namespace Isoform.Document;

/// <summary>A rigid placement: origin, x direction and normal (z); y = normal × x.</summary>
public sealed record Placement(double[] Origin, double[] X, double[] Normal)
{
    public static Placement Identity => new([0, 0, 0], [1, 0, 0], [0, 0, 1]);
}

/// <summary>
/// One occurrence in an assembly. <paramref name="Part"/> names a part or assembly document (a path the caller
/// resolves). <paramref name="Hash"/>, when given, pins the document's content hash, so a changed part is noticed.
/// The id is stable: occurrence names are built from it (<c>left/plate/z+</c>), never from list position.
/// </summary>
public sealed record Component(string Id, string Part, Placement Placement, string? Hash = null);

/// <summary>An assembly: placed occurrences of parts and sub-assemblies. Like a part, only this text is versioned.</summary>
public sealed record AssemblyDocument(IReadOnlyList<Component> Components)
{
    public const string FormatVersion = "isoform-assembly/0";
}

/// <summary>An assembly with the documents it uses in one file, for sharing and for the playground.</summary>
public sealed record Bundle(AssemblyDocument Assembly, IReadOnlyDictionary<string, string> Documents)
{
    public const string FormatVersion = "isoform-bundle/0";
}
