using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Step;

/// <summary>An exact boundary representation: shared vertices and edges, faces on analytic surfaces, one or more solids.</summary>
public sealed record Brep(IReadOnlyList<BrepVertex> Vertices, IReadOnlyList<BrepEdge> Edges, IReadOnlyList<BrepSolid> Solids);

public sealed record BrepVertex(int Index, Point3D Point);

/// <summary>An edge from <see cref="Start"/> to <see cref="End"/> in the curve's direction; a closed edge has Start == End.</summary>
public sealed record BrepEdge(int Index, Curve Curve, int Start, int End)
{
    public bool IsClosed => Start == End;
}

/// <summary>An edge as one loop uses it: along its direction or against it.</summary>
public readonly record struct BrepCoedge(int Edge, bool Forward);

/// <summary>A boundary loop with the face on its left seen from outside. Outer: a face's only loop, or a planar face's counter-clockwise one.</summary>
public sealed record BrepLoop(IReadOnlyList<BrepCoedge> Coedges, bool Outer);

/// <summary>
/// A face on <see cref="Surface"/>. <see cref="SameSense"/> says whether the surface normal (the gradient of its
/// distance) points out of the solid; <see cref="Sources"/> and <see cref="TopologyFace"/> tie it to the Isoform result.
/// </summary>
public sealed record BrepFace(int TopologyFace, IReadOnlyList<string> Sources, Surface Surface, bool SameSense, IReadOnlyList<BrepLoop> Loops);

/// <summary>A solid bounded by one closed shell.</summary>
public sealed record BrepSolid(string Name, IReadOnlyList<BrepFace> Faces);
