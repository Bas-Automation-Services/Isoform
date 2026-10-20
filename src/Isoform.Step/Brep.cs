using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Step;

/// <summary>An exact boundary representation: shared vertices and edges, faces on analytic surfaces, one or more solids.</summary>
public sealed record Brep(IReadOnlyList<BrepVertex> Vertices, IReadOnlyList<BrepEdge> Edges, IReadOnlyList<BrepSolid> Solids);

public sealed record BrepVertex(int Index, Point3D Point);

/// <summary>
/// An edge from <see cref="Start"/> to <see cref="End"/> in the curve's direction, over the curve parameters
/// <see cref="T0"/> to <see cref="T1"/> (T0 &lt; T1); a closed edge has Start == End.
/// </summary>
public sealed record BrepEdge(int Index, Curve Curve, int Start, int End, double T0, double T1)
{
    public bool IsClosed => Start == End;
}

/// <summary>
/// An edge as one loop uses it: along its direction or against it, with its image in the face's (u, v) parameters
/// (null on a plane, whose parameters any reader projects exactly).
/// </summary>
public readonly record struct BrepCoedge(int Edge, bool Forward, UvCurve? Uv = null);

/// <summary>A boundary loop with the face on its left seen from outside. Outer: a face's only loop, or a planar face's counter-clockwise one.</summary>
public sealed record BrepLoop(IReadOnlyList<BrepCoedge> Coedges, bool Outer);

/// <summary>
/// A face on <see cref="Surface"/>. <see cref="SameSense"/> says whether the surface normal (the gradient of its
/// distance) points out of the solid; <see cref="Sources"/> and <see cref="TopologyFace"/> tie it to the Isoform result.
/// The surface's parameters follow ISO 10303-42 in the placement (Origin, Axis, Perpendicular(Axis)).
/// </summary>
public sealed record BrepFace(int TopologyFace, IReadOnlyList<string> Sources, Surface Surface, bool SameSense, IReadOnlyList<BrepLoop> Loops);

/// <summary>A closed, connected set of faces.</summary>
public sealed record BrepShell(IReadOnlyList<BrepFace> Faces);

/// <summary>A solid bounded by an outer shell and any number of inner shells (voids, such as a shell's cavity).</summary>
public sealed record BrepSolid(string Name, BrepShell Outer, IReadOnlyList<BrepShell> Voids)
{
    /// <summary>Every face: the outer shell's, then the voids'.</summary>
    public IReadOnlyList<BrepFace> Faces => [.. Outer.Faces, .. Voids.SelectMany(v => v.Faces)];
}
