using BasAutomation.Geometry;
using Isoform.Step;
using IsoCircle = Isoform.Geometry.Circle;
using IsoCurve = Isoform.Geometry.Curve;
using IsoLine = Isoform.Geometry.Line;
using IsoSurface = Isoform.Geometry.Surface;
using SurfaceKind = Isoform.Geometry.SurfaceKind;

namespace Isoform.Brep.Modeling;

/// <summary>
/// Kernel bodies as Isoform's exact B-rep (<see cref="Isoform.Step.Brep"/>), which <see cref="StepWriter"/> writes.
/// Curves and surfaces map one to one; p-curves are the kernel's own, moved into the parameters Isoform's STEP
/// placements define (on a cylinder, u turns by the angle between the two x axes).
/// </summary>
public static class StepConversion
{
    public static Step.Brep ToStep(IReadOnlyList<NamedBody> bodies, string name = "part")
    {
        var vertices = new List<BrepVertex>();
        var edges = new List<BrepEdge>();
        var solids = new List<BrepSolid>();
        var faceOffset = 0;
        for (var b = 0; b < bodies.Count; b++)
        {
            var (body, names) = (bodies[b].Body, bodies[b].Names);
            var (v0, e0) = (vertices.Count, edges.Count);
            foreach (var v in body.Vertices) vertices.Add(new BrepVertex(vertices.Count, v.Point));
            foreach (var e in body.Edges) edges.Add(new BrepEdge(edges.Count, Curve(e.Curve), v0 + e.Start.Index, v0 + e.End.Index, e.T0, e.T1));

            BrepShell Shell(Shell shell) => new([.. shell.Faces.Select(f =>
            {
                var face = body[f];
                var (surface, shift) = Surface(face.Surface, faceOffset + f.Index, names[f]);
                var loops = face.Loops.Select((l, i) => new BrepLoop([.. body.CoedgesOf(l).Select(c =>
                {
                    var coedge = body[c];
                    var edge = body[coedge.Edge];
                    UvCurve? uv = null;
                    if (surface.Kind != SurfaceKind.Plane)
                    {
                        var (a, z) = (coedge.Pcurve.At(edge.T0), coedge.Pcurve.At(edge.T1));
                        uv = new UvCurve(1, [(a.X + shift, a.Y), (z.X + shift, z.Y)], [edge.T0, edge.T1], [2, 2]);
                    }
                    return new BrepCoedge(e0 + coedge.Edge.Index, coedge.Forward, uv);
                })], Outer: i == 0)).ToList();
                return new BrepFace(faceOffset + f.Index, [names[f]], surface, face.SameSense, loops);
            })]);

            solids.Add(new BrepSolid(bodies.Count == 1 ? name : $"{name}-{b + 1}", Shell(body.Shells[0]), [.. body.Shells.Skip(1).Select(Shell)]));
            faceOffset += body.Faces.Length;
        }
        return new Step.Brep(vertices, edges, solids);
    }

    static IsoCurve Curve(Curve3 c) => c switch
    {
        Line3 l => new IsoLine(l.Origin, l.Direction),
        Circle3 k => new IsoCircle(k.Frame.Origin, k.Frame.ZAxis, k.Frame.XAxis, k.Radius),
        _ => throw new NotSupportedException(c.GetType().Name),
    };

    // The Isoform surface, and what to add to the kernel's u to get its parameter there.
    static (IsoSurface, double) Surface(Surface s, int id, string source)
    {
        switch (s)
        {
            case Plane p:
                return (new IsoSurface(id, source, SurfaceKind.Plane, p.Frame.Origin, p.Frame.ZAxis), 0);
            case Cylinder c:
                var surface = new IsoSurface(id, source, SurfaceKind.Cylinder, c.Frame.Origin, c.Frame.ZAxis, c.Radius);
                var (x, y, _) = SurfaceParameters.Axes(surface);
                return (surface, Math.Atan2(c.Frame.XAxis.Dot(y), c.Frame.XAxis.Dot(x)));
            default:
                throw new NotSupportedException(s.GetType().Name);
        }
    }
}
