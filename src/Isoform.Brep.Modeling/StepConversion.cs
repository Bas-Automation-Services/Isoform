using BasAutomation.Geometry;
using Isoform.Step;
using IsoCircle = Isoform.Geometry.Circle;
using IsoCurve = Isoform.Geometry.Curve;
using IsoEllipse = Isoform.Geometry.Ellipse;
using IsoLine = Isoform.Geometry.Line;
using IsoSurface = Isoform.Geometry.Surface;
using SurfaceKind = Isoform.Geometry.SurfaceKind;

namespace Isoform.Brep.Modeling;

/// <summary>
/// Kernel bodies as Isoform's exact B-rep (<see cref="Isoform.Step.Brep"/>), which <see cref="StepWriter"/> writes,
/// with the conventions of Isoform's own export (<see cref="ExactBrep"/>): edges parametrised to start at π/2 on a
/// circle (in [π/2, 3π/2] on an ellipse), clear of the 0 / 2π wrap where readers differ; images in a curved face's
/// parameters from <see cref="SurfaceParameters.Image"/>, each shifted by whole turns to start where the one before
/// it ended, the first within a turn of π.
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
            foreach (var e in body.Edges) edges.Add(Finish(edges.Count, e, body, v0));

            BrepShell Shell(Shell shell) => new([.. shell.Faces.Select(f =>
            {
                var face = body[f];
                var surface = Surface(face.Surface, faceOffset + f.Index, string.Join("+", names[f]));
                var loops = face.Loops.Select((l, i) => new BrepLoop([.. SeamFirst(body, l, face.SameSense).Select(c =>
                    new BrepCoedge(e0 + body[c].Edge.Index, body[c].Forward, null))], Outer: i == 0)).ToList();
                return new BrepFace(faceOffset + f.Index, names[f], surface, face.SameSense, WithImages(surface, loops, edges));
            })]);

            solids.Add(new BrepSolid(bodies.Count == 1 ? name : $"{name}-{b + 1}", Shell(body.Shells[0]), [.. body.Shells.Skip(1).Select(Shell)]));
            faceOffset += body.Faces.Length;
        }
        return new Step.Brep(vertices, edges, solids);
    }

    // A loop's coedges, started so that a seam's last use as written runs as the face is oriented (along the edge on a
    // face with its surface's sense, against it otherwise): OCCT's reader gives a seam's p-curves to its uses one at a
    // time and the last decides (ExactBrep.SeamsForwardLast turns the edge round for the same reason).
    static IEnumerable<CoedgeId> SeamFirst(Body body, LoopId loop, bool sameSense)
    {
        var coedges = body.CoedgesOf(loop).ToList();
        var seam = coedges.GroupBy(c => body[c].Edge).FirstOrDefault(g => g.Count() == 2);
        if (seam is null) return coedges;
        var first = coedges.FindIndex(c => body[c].Edge == seam.Key && body[c].Forward != sameSense);
        return [.. coedges.Skip(first), .. coedges.Take(first)];
    }

    // The edge on Isoform's curve, parametrised as Isoform's export does.
    static BrepEdge Finish(int index, Edge e, Body body, int v0)
    {
        var (p, q) = (body[e.Start].Point, body[e.End].Point);
        var (start, end) = (v0 + e.Start.Index, v0 + e.End.Index);
        const double quarter = Math.PI / 2;
        static double Turn(double a) => a - 2 * Math.PI * Math.Floor(a / (2 * Math.PI));
        switch (e.Curve)
        {
            case Line3 l:
                return new BrepEdge(index, new IsoLine(p, l.Direction), start, end, 0, (q - p).Dot(l.Direction));
            case Circle3 k:
            {
                var radial = p - k.Frame.Origin;
                var n = k.Frame.ZAxis;
                var circle = new IsoCircle(k.Frame.Origin, n, (radial - n * radial.Dot(n)).Normalized().Cross(n), k.Radius);
                return new BrepEdge(index, circle, start, end, quarter, quarter + (e.IsClosed ? 2 * Math.PI : Turn(circle.Parameter(q) - quarter)));
            }
            case Ellipse3 k:
            {
                var ellipse = new IsoEllipse(k.Frame.Origin, k.Frame.ZAxis, k.Frame.XAxis, k.Major, k.Minor);
                var t = Turn(ellipse.Parameter(p));
                if (t < quarter || t > 3 * quarter) (ellipse, t) = (ellipse with { X = -ellipse.X }, Turn(t + Math.PI));
                return new BrepEdge(index, ellipse, start, end, t, t + (e.IsClosed ? 2 * Math.PI : Turn(ellipse.Parameter(q) - t)));
            }
            default:
                throw new NotSupportedException(e.Curve.GetType().Name);
        }
    }

    // A curved face's loops with every coedge's image, each starting where the one before it ended.
    static List<BrepLoop> WithImages(IsoSurface s, List<BrepLoop> loops, List<BrepEdge> edges)
    {
        if (s.Kind == SurfaceKind.Plane) return loops;
        var images = new Dictionary<int, UvCurve>();
        (double U, double V)? anchor = null;
        var result = new List<BrepLoop>();
        foreach (var loop in loops)
        {
            var coedges = new List<BrepCoedge>();
            var last = anchor;
            foreach (var c in loop.Coedges)
            {
                var e = edges[c.Edge];
                if (!images.TryGetValue(c.Edge, out var image)) images[c.Edge] = image = SurfaceParameters.Image(s, e.Curve, e.T0, e.T1);
                var from = c.Forward ? image.Start : image.End;
                var to = last ?? (Math.PI, Math.PI);
                image = Snapped(image.Shifted(SurfaceParameters.Turns(to.U - from.U), SurfaceParameters.PeriodicV(s) ? SurfaceParameters.Turns(to.V - from.V) : 0));
                coedges.Add(c with { Uv = image });
                anchor ??= c.Forward ? image.Start : image.End;
                last = c.Forward ? image.End : image.Start;
            }
            result.Add(loop with { Coedges = coedges });
        }
        return result;
    }

    // An image with every u that rounding left a hair off a whole turn put on it: a seam at u = 0 written at −2e-16
    // is read a turn away.
    static UvCurve Snapped(UvCurve image) => image with
    {
        Poles = [.. image.Poles.Select(p =>
        {
            var turn = 2 * Math.PI * Math.Round(p.U / (2 * Math.PI));
            return Math.Abs(p.U - turn) <= 1e-12 ? (turn, p.V) : p;
        })],
    };

    static IsoSurface Surface(Surface s, int id, string source) => s switch
    {
        Plane p => new IsoSurface(id, source, SurfaceKind.Plane, p.Frame.Origin, p.Frame.ZAxis),
        Cylinder c => new IsoSurface(id, source, SurfaceKind.Cylinder, c.Frame.Origin, c.Frame.ZAxis, c.Radius),
        Torus t => new IsoSurface(id, source, SurfaceKind.Torus, t.Frame.Origin, t.Frame.ZAxis, t.Major, t.Minor),
        // Isoform's cone stands on its apex and opens along its axis; the normals agree, both away from the axis.
        Cone k => new IsoSurface(id, source, SurfaceKind.Cone, k.Apex, k.Frame.ZAxis * Math.Sign(k.Slope), Angle: Math.Atan(Math.Abs(k.Slope))),
        _ => throw new NotSupportedException(s.GetType().Name),
    };
}
