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
            var v0 = vertices.Count;
            foreach (var v in body.Vertices) vertices.Add(new BrepVertex(vertices.Count, v.Point));
            // A pole's edge is not written (a loop through a pole closes there, as Isoform's export has it).
            var edgeOf = new int[body.Edges.Length];
            for (var i = 0; i < body.Edges.Length; i++)
            {
                edgeOf[i] = body.Edges[i].Curve is Pole ? -1 : edges.Count;
                if (edgeOf[i] >= 0) edges.Add(Finish(edges.Count, body.Edges[i], body, v0));
            }

            BrepShell Shell(Shell shell, bool isVoid) => new([.. shell.Faces.Select(f =>
            {
                var face = body[f];
                var surface = Surface(face.Surface, faceOffset + f.Index, string.Join("+", names[f]));
                // Each coedge, or for a pole how far it turns u.
                var loops = face.Loops.Select(l => SeamFirst(body, l, face.SameSense, isVoid).Select(c =>
                {
                    if (edgeOf[body[c].Edge.Index] >= 0) return (Coedge: (BrepCoedge?)new BrepCoedge(edgeOf[body[c].Edge.Index], body[c].Forward, null), Turn: 0.0);
                    var (from, to) = body.ParametersOf(c);
                    return (null, body[c].Pcurve.At(to).X - body[c].Pcurve.At(from).X);
                }).ToList()).ToList();
                return new BrepFace(faceOffset + f.Index, names[f], surface, face.SameSense, WithImages(surface, loops, edges));
            })]);

            solids.Add(new BrepSolid(bodies.Count == 1 ? name : $"{name}-{b + 1}", Shell(body.Shells[0], false), [.. body.Shells.Skip(1).Select(s => Shell(s, true))]));
            faceOffset += body.Faces.Length;
        }
        return new Step.Brep(vertices, edges, solids);
    }

    // A loop's coedges, started so that a seam's last use as written runs as the face is oriented (along the edge on a
    // face with its surface's sense, against it otherwise): OCCT's reader gives a seam's p-curves to its uses one at a
    // time and the last decides (ExactBrep.SeamsForwardLast turns the edge round for the same reason). The writer turns
    // a void's loops round, so there it is the first use.
    static IEnumerable<CoedgeId> SeamFirst(Body body, LoopId loop, bool sameSense, bool turned)
    {
        var coedges = body.CoedgesOf(loop).ToList();
        var seam = coedges.GroupBy(c => body[c].Edge).FirstOrDefault(g => g.Count() == 2);
        if (seam is null) return coedges;
        var first = coedges.FindIndex(c => body[c].Edge == seam.Key && body[c].Forward != sameSense != turned);
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

    // A curved face's loops with every coedge's image, each starting where the one before it ended (or across a pole,
    // where the one before it ended moved along u as far as the pole turns).
    static List<BrepLoop> WithImages(IsoSurface s, List<List<(BrepCoedge? Coedge, double Turn)>> loops, List<BrepEdge> edges)
    {
        if (s.Kind == SurfaceKind.Plane) return [.. loops.Select((l, i) => new BrepLoop([.. l.Select(c => c.Coedge!.Value)], Outer: i == 0))];
        var images = new Dictionary<int, UvCurve>();
        (double U, double V)? anchor = null;
        var result = new List<BrepLoop>();
        foreach (var (loop, index) in loops.Select((l, i) => (l, i)))
        {
            var coedges = new List<BrepCoedge>();
            var last = anchor;
            foreach (var (coedge, turn) in loop)
            {
                if (coedge is not { } c)
                {
                    if (last is { } at) last = (at.U + turn, at.V);
                    continue;
                }
                var e = edges[c.Edge];
                if (!images.TryGetValue(c.Edge, out var image)) images[c.Edge] = image = SurfaceParameters.Image(s, e.Curve, e.T0, e.T1);
                var from = c.Forward ? image.Start : image.End;
                var to = last ?? (Math.PI, Math.PI);
                image = Snapped(image.Shifted(SurfaceParameters.Turns(to.U - from.U), SurfaceParameters.PeriodicV(s) ? SurfaceParameters.Turns(to.V - from.V) : 0));
                coedges.Add(c with { Uv = image });
                anchor ??= c.Forward ? image.Start : image.End;
                last = c.Forward ? image.End : image.Start;
            }
            result.Add(new BrepLoop(coedges, Outer: index == 0));
        }
        return result;
    }

    // An image with every u that rounding left a hair off a whole turn put on it (a seam at u = 0 written at −2e-16
    // is read a turn away), and one that runs along v or u kept exactly straight (a seam whose u drifts down by 1e-15
    // has OCCT's reader take its two images the wrong way round).
    static UvCurve Snapped(UvCurve image)
    {
        var (u0, v0) = image.Poles[0];
        var alongV = image.Poles.All(p => Math.Abs(p.U - u0) <= 1e-12);
        var alongU = image.Poles.All(p => Math.Abs(p.V - v0) <= 1e-12);
        return image with
        {
            Poles = [.. image.Poles.Select(p =>
            {
                var u = alongV ? u0 : p.U;
                var turn = 2 * Math.PI * Math.Round(u / (2 * Math.PI));
                return (Math.Abs(u - turn) <= 1e-12 ? turn : u, alongU ? v0 : p.V);
            })],
        };
    }

    static IsoSurface Surface(Surface s, int id, string source) => s switch
    {
        Plane p => new IsoSurface(id, source, SurfaceKind.Plane, p.Frame.Origin, p.Frame.ZAxis),
        Cylinder c => new IsoSurface(id, source, SurfaceKind.Cylinder, c.Frame.Origin, c.Frame.ZAxis, c.Radius),
        Torus t => new IsoSurface(id, source, SurfaceKind.Torus, t.Frame.Origin, t.Frame.ZAxis, t.Major, t.Minor),
        // Isoform's cone stands on its apex and opens along its axis; the normals agree, both away from the axis.
        Cone k => new IsoSurface(id, source, SurfaceKind.Cone, k.Apex, k.Frame.ZAxis * Math.Sign(k.Slope), Angle: Math.Atan(Math.Abs(k.Slope))),
        Sphere o => new IsoSurface(id, source, SurfaceKind.Sphere, o.Frame.Origin, o.Frame.ZAxis, o.Radius),
        _ => throw new NotSupportedException(s.GetType().Name),
    };
}
