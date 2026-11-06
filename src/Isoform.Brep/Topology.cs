using System.Collections.Immutable;
using BasAutomation.Geometry;

namespace Isoform.Brep;

// Typed handles: an index into one of the body's arrays. A handle of one kind cannot be passed as another.
public readonly record struct VertexId(int Index);
public readonly record struct EdgeId(int Index);
public readonly record struct CoedgeId(int Index);
public readonly record struct LoopId(int Index);
public readonly record struct FaceId(int Index);
public readonly record struct ShellId(int Index);

/// <summary>A point where edges end. <see cref="Tolerance"/>: how far the edges' ends may be from it.</summary>
public readonly record struct Vertex(Point3D Point, double Tolerance);

/// <summary>
/// The curve between two vertices over <see cref="T0"/> to <see cref="T1"/> (T0 &lt; T1), running from
/// <see cref="Start"/> to <see cref="End"/>. A closed edge (a full circle) starts and ends at one vertex.
/// <see cref="Tolerance"/>: how far its faces' p-curves may lie from the curve.
/// </summary>
public readonly record struct Edge(Curve3 Curve, double T0, double T1, VertexId Start, VertexId End, double Tolerance)
{
    public bool IsClosed => Start == End;
}

/// <summary>
/// One use of an edge by a loop: along the edge (<see cref="Forward"/>) or against it, with the edge's image in the
/// face's (u, v) parameters. The p-curve takes the edge's own parameter: <c>Surface.At(Pcurve.At(t)) ≈ Curve.At(t)</c>
/// for t in [T0, T1] (same parameter). A seam edge is used twice by one face, with two p-curves a period apart.
/// </summary>
public readonly record struct Coedge(EdgeId Edge, bool Forward, LoopId Loop, CoedgeId Next, Curve2 Pcurve);

/// <summary>A closed chain of coedges with its face on the left, seen from outside the solid.</summary>
public readonly record struct Loop(FaceId Face, CoedgeId First);

/// <summary>
/// A bounded piece of a surface. <see cref="SameSense"/>: the surface normal points out of the solid. The first
/// loop is the outer boundary, any others are holes.
/// </summary>
public readonly record struct Face(Surface Surface, bool SameSense, ShellId Shell, ImmutableArray<LoopId> Loops);

/// <summary>A connected set of faces that closes off a volume.</summary>
public readonly record struct Shell(ImmutableArray<FaceId> Faces);

/// <summary>
/// An immutable boundary representation: flat arrays of vertices, edges, coedges, loops, faces and shells that refer to
/// each other by typed handles. The first shell bounds the solid from outside; any further shells are voids.
/// Manifold: every edge has exactly two uses in a valid body (<see cref="BodyCheck"/> checks it).
/// </summary>
public sealed class Body
{
    /// <summary>The default modelling tolerance in mm (Isoform's, and OCCT's <c>Precision::Confusion</c>).</summary>
    public const double DefaultTolerance = 1e-7;

    internal Body(double tolerance, ImmutableArray<Vertex> vertices, ImmutableArray<Edge> edges, ImmutableArray<Coedge> coedges,
        ImmutableArray<Loop> loops, ImmutableArray<Face> faces, ImmutableArray<Shell> shells, ImmutableArray<ImmutableArray<CoedgeId>> uses)
    {
        (Tolerance, Vertices, Edges, Coedges, Loops, Faces, Shells, this.uses) = (tolerance, vertices, edges, coedges, loops, faces, shells, uses);
    }

    readonly ImmutableArray<ImmutableArray<CoedgeId>> uses;

    public double Tolerance { get; }
    public ImmutableArray<Vertex> Vertices { get; }
    public ImmutableArray<Edge> Edges { get; }
    public ImmutableArray<Coedge> Coedges { get; }
    public ImmutableArray<Loop> Loops { get; }
    public ImmutableArray<Face> Faces { get; }
    public ImmutableArray<Shell> Shells { get; }

    public Vertex this[VertexId id] => Vertices[id.Index];
    public Edge this[EdgeId id] => Edges[id.Index];
    public Coedge this[CoedgeId id] => Coedges[id.Index];
    public Loop this[LoopId id] => Loops[id.Index];
    public Face this[FaceId id] => Faces[id.Index];
    public Shell this[ShellId id] => Shells[id.Index];

    /// <summary>The coedges that use an edge: two in a valid body.</summary>
    public ImmutableArray<CoedgeId> UsesOf(EdgeId edge) => uses[edge.Index];

    /// <summary>The coedges of a loop, in order from its first.</summary>
    public IEnumerable<CoedgeId> CoedgesOf(LoopId loop)
    {
        var first = Loops[loop.Index].First;
        var c = first;
        do
        {
            yield return c;
            c = Coedges[c.Index].Next;
        } while (c != first);
    }

    /// <summary>The face a coedge bounds.</summary>
    public FaceId FaceOf(CoedgeId coedge) => Loops[Coedges[coedge.Index].Loop.Index].Face;

    /// <summary>The vertex a coedge leaves from, in the loop's direction.</summary>
    public VertexId StartOf(CoedgeId coedge)
    {
        var c = Coedges[coedge.Index];
        var e = Edges[c.Edge.Index];
        return c.Forward ? e.Start : e.End;
    }

    /// <summary>The vertex a coedge arrives at, in the loop's direction.</summary>
    public VertexId EndOf(CoedgeId coedge)
    {
        var c = Coedges[coedge.Index];
        var e = Edges[c.Edge.Index];
        return c.Forward ? e.End : e.Start;
    }

    /// <summary>The edge parameters a coedge runs over, in the loop's direction.</summary>
    public (double From, double To) ParametersOf(CoedgeId coedge)
    {
        var c = Coedges[coedge.Index];
        var e = Edges[c.Edge.Index];
        return c.Forward ? (e.T0, e.T1) : (e.T1, e.T0);
    }
}

/// <summary>
/// Assembles a <see cref="Body"/>. Operations add entities through it and freeze the result; it checks nothing that
/// <see cref="BodyCheck"/> can report, so a builder can also make the broken bodies the checks are tested on.
/// </summary>
public sealed class BodyBuilder(double tolerance = Body.DefaultTolerance)
{
    /// <summary>One use of an edge in a loop being added.</summary>
    public readonly record struct Use(EdgeId Edge, bool Forward, Curve2 Pcurve);

    readonly List<Vertex> vertices = [];
    readonly List<Edge> edges = [];
    readonly List<(FaceId Face, IReadOnlyList<Use> Uses)> loops = [];
    readonly List<(Surface Surface, bool SameSense, ShellId Shell)> faces = [];
    int shells;

    public double Tolerance => tolerance;

    public VertexId AddVertex(Point3D point)
    {
        vertices.Add(new Vertex(point, tolerance));
        return new VertexId(vertices.Count - 1);
    }

    public EdgeId AddEdge(Curve3 curve, double t0, double t1, VertexId start, VertexId end)
    {
        edges.Add(new Edge(curve, t0, t1, start, end, tolerance));
        return new EdgeId(edges.Count - 1);
    }

    public ShellId AddShell() => new(shells++);

    public FaceId AddFace(ShellId shell, Surface surface, bool sameSense)
    {
        faces.Add((surface, sameSense, shell));
        return new FaceId(faces.Count - 1);
    }

    /// <summary>Adds a loop to a face: its first loop is the outer boundary.</summary>
    public LoopId AddLoop(FaceId face, IReadOnlyList<Use> uses)
    {
        if (uses.Count == 0) throw new ArgumentException("A loop needs at least one coedge.", nameof(uses));
        loops.Add((face, uses));
        return new LoopId(loops.Count - 1);
    }

    public Body Build()
    {
        var count = new int[edges.Count];
        var total = 0;
        foreach (var (_, uses) in loops)
        foreach (var u in uses)
        {
            count[u.Edge.Index]++;
            total++;
        }
        var edgeUses = count.Select(n => new CoedgeId[n]).ToArray();
        var filled = new int[edges.Count];
        var coedges = ImmutableArray.CreateBuilder<Coedge>(total);
        var loopArray = ImmutableArray.CreateBuilder<Loop>(loops.Count);
        var loopsOf = faces.Select(_ => new List<LoopId>(1)).ToArray();
        for (var l = 0; l < loops.Count; l++)
        {
            var (face, uses) = loops[l];
            var first = coedges.Count;
            for (var i = 0; i < uses.Count; i++)
            {
                var next = new CoedgeId(first + (i + 1) % uses.Count);
                var e = uses[i].Edge.Index;
                edgeUses[e][filled[e]++] = new CoedgeId(coedges.Count);
                coedges.Add(new Coedge(uses[i].Edge, uses[i].Forward, new LoopId(l), next, uses[i].Pcurve));
            }
            loopArray.Add(new Loop(face, new CoedgeId(first)));
            loopsOf[face.Index].Add(new LoopId(l));
        }

        var facesOf = Enumerable.Range(0, shells).Select(_ => new List<FaceId>()).ToArray();
        for (var f = 0; f < faces.Count; f++) facesOf[faces[f].Shell.Index].Add(new FaceId(f));
        var faceArray = faces.Select((f, i) => new Face(f.Surface, f.SameSense, f.Shell, [.. loopsOf[i]])).ToImmutableArray();
        var shellArray = facesOf.Select(f => new Shell([.. f])).ToImmutableArray();

        return new Body(tolerance, [.. vertices], [.. edges], coedges.MoveToImmutable(), loopArray.MoveToImmutable(), faceArray, shellArray,
            [.. edgeUses.Select(System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray)]);
    }
}
