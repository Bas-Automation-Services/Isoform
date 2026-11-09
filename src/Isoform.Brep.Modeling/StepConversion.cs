using BasAutomation.Geometry;
using Isoform.Step;
using IsoBSpline = Isoform.Geometry.BSpline;
using IsoCircle = Isoform.Geometry.Circle;
using IsoCurve = Isoform.Geometry.Curve;
using IsoEllipse = Isoform.Geometry.Ellipse;
using IsoHyperbola = Isoform.Geometry.Hyperbola;
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
            var flip = SeamsForwardLast(body);
            // A pole's edge is not written (a loop through a pole closes there, as Isoform's export has it).
            var edgeOf = new int[body.Edges.Length];
            for (var i = 0; i < body.Edges.Length; i++)
            {
                edgeOf[i] = body.Edges[i].Curve is Pole ? -1 : edges.Count;
                if (edgeOf[i] >= 0) edges.Add(Finish(edges.Count, body.Edges[i], body, v0, flip[i]));
            }

            BrepShell Shell(Shell shell, bool isVoid) => new([.. shell.Faces.Select(f =>
            {
                var face = body[f];
                var surface = Surface(face.Surface, faceOffset + f.Index, string.Join("+", names[f]));
                // Each coedge, or for a pole how far it turns u.
                var loops = face.Loops.Select(l => body.CoedgesOf(l).Select(c =>
                {
                    var e = body[c].Edge.Index;
                    if (edgeOf[e] >= 0) return (Coedge: (BrepCoedge?)new BrepCoedge(edgeOf[e], body[c].Forward != flip[e], null), Turn: 0.0);
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

    // The seams to turn round, so that each one's last use in its loop as written runs as the face is oriented (along
    // the edge on a face with its surface's sense, against it otherwise): OCCT's reader gives a seam's p-curves to its
    // uses one at a time and the last decides (ExactBrep.SeamsForwardLast does the same). One loop can hold several
    // seams (a seam cut in pieces by a hole across it). The writer turns a void's loops round, so there it is the
    // first use.
    static bool[] SeamsForwardLast(Body body)
    {
        var flip = new bool[body.Edges.Length];
        for (var s = 0; s < body.Shells.Length; s++)
        foreach (var f in body.Shells[s].Faces)
        foreach (var l in body[f].Loops)
        foreach (var seam in body.CoedgesOf(l).GroupBy(c => body[c].Edge).Where(g => g.Count() == 2))
        {
            var use = s > 0 ? seam.First() : seam.Last();
            flip[seam.Key.Index] = body[use].Forward != body[f].SameSense;
        }
        return flip;
    }

    // The edge on Isoform's curve, parametrised as Isoform's export does; a seam turned round where it must be.
    static BrepEdge Finish(int index, Edge e, Body body, int v0, bool turned = false)
    {
        var (p, q) = (body[e.Start].Point, body[e.End].Point);
        var (start, end) = (v0 + e.Start.Index, v0 + e.End.Index);
        if (turned)
        {
            (p, q, start, end) = (q, p, end, start);
            e = e.Curve switch
            {
                Line3 l => e with { Curve = l with { Direction = -l.Direction } },
                Circle3 k => e with { Curve = new Circle3(RigidTransform3D.FromFrame(k.Frame.Origin, -k.Frame.ZAxis, k.Frame.XAxis), k.Radius) },
                _ => throw new NotSupportedException($"a seam on a {e.Curve.GetType().Name}"),
            };
        }
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
            case Hyperbola3 k:
            {
                // An open branch: turned (Y the other way) where needed, so that the edge runs up its parameter.
                var hyperbola = new IsoHyperbola(k.Frame.Origin, k.Frame.ZAxis, k.Frame.XAxis, k.Major, k.Minor);
                if (hyperbola.Parameter(q) < hyperbola.Parameter(p)) hyperbola = hyperbola with { Normal = -hyperbola.Normal };
                return new BrepEdge(index, hyperbola, start, end, hyperbola.Parameter(p), hyperbola.Parameter(q));
            }
            case Trace3 k:
                return new BrepEdge(index, Spline(k, e.T0, e.T1), start, end, e.T0, e.T1);
            default:
                throw new NotSupportedException(e.Curve.GetType().Name);
        }
    }

    // A traced curve over [t0, t1] as degree-9 Bézier pieces through the curve itself at Chebyshev points, in as many
    // equal pieces as keep them within 1e-9 of both its surfaces halfway between those points: the kernel's spline has a
    // span every few tenths of a millimetre, which readers work through one by one. Its parameter is smooth only within
    // those spans, so the pieces' points can run a little along the curve from the spline's at the same t: the faces'
    // images, made from these pieces, follow them.
    static IsoBSpline Spline(Trace3 c, double t0, double t1)
    {
        const int degree = 9;
        var nodes = Enumerable.Range(0, degree + 1).Select(i => (1 - Math.Cos(Math.PI * i / degree)) / 2).ToArray();
        var basis = new double[degree + 1, degree + 1];
        for (var i = 0; i <= degree; i++)
            for (var j = 0; j <= degree; j++) basis[i, j] = Bernstein(degree, j, nodes[i]);
        for (var spans = Math.Max(1, (int)Math.Ceiling((t1 - t0) / (Math.PI / 8))); ; spans *= 2)
        {
            var knots = Enumerable.Range(0, spans + 1).Select(k => k == spans ? t1 : t0 + (t1 - t0) * k / spans).ToList();
            var poles = new List<Point3D>();
            var error = 0.0;
            for (var k = 0; k < spans; k++)
            {
                var (a, b) = (knots[k], knots[k + 1]);
                var at = nodes.Select(x => c.Exact(a + (b - a) * x)).ToArray();
                var (x0, y0, z0) = (Solve(basis, [.. at.Select(p => p.X)]), Solve(basis, [.. at.Select(p => p.Y)]), Solve(basis, [.. at.Select(p => p.Z)]));
                // The ends exactly the curve's, so that pieces meet.
                (x0[0], y0[0], z0[0]) = (at[0].X, at[0].Y, at[0].Z);
                (x0[degree], y0[degree], z0[degree]) = (at[^1].X, at[^1].Y, at[^1].Z);
                for (var j = k == 0 ? 0 : 1; j <= degree; j++) poles.Add(new Point3D(x0[j], y0[j], z0[j]));
                for (var i = 0; i < degree; i++)
                {
                    var x = (nodes[i] + nodes[i + 1]) / 2;
                    var (px, py, pz) = (0.0, 0.0, 0.0);
                    for (var j = 0; j <= degree; j++)
                    {
                        var w = Bernstein(degree, j, x);
                        (px, py, pz) = (px + x0[j] * w, py + y0[j] * w, pz + z0[j] * w);
                    }
                    error = Math.Max(error, c.Gap(new Point3D(px, py, pz)));
                }
            }
            if (error <= 1e-9 || spans >= 512) return new IsoBSpline(degree, poles, knots);
        }
    }

    static double Bernstein(int n, int j, double x)
    {
        var binomial = 1.0;
        for (var i = 1; i <= j; i++) binomial = binomial * (n - j + i) / i;
        return binomial * Math.Pow(x, j) * Math.Pow(1 - x, n - j);
    }

    // Gaussian elimination with partial pivoting (a small, well-conditioned system).
    static double[] Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (var c = 0; c < n; c++)
        {
            var p = Enumerable.Range(c, n - c).MaxBy(r => Math.Abs(m[r, c]));
            for (var k = 0; k < n; k++) (m[c, k], m[p, k]) = (m[p, k], m[c, k]);
            (x[c], x[p]) = (x[p], x[c]);
            for (var r = c + 1; r < n; r++)
            {
                var f = m[r, c] / m[c, c];
                for (var k = c; k < n; k++) m[r, k] -= f * m[c, k];
                x[r] -= f * x[c];
            }
        }
        for (var r = n - 1; r >= 0; r--)
        {
            for (var k = r + 1; k < n; k++) x[r] -= m[r, k] * x[k];
            x[r] /= m[r, r];
        }
        return x;
    }

    // A curved face's loops with every coedge's image, each starting where the one before it ended (or across a pole,
    // where the one before it ended moved along u as far as the pole turns).
    static List<BrepLoop> WithImages(IsoSurface s, List<List<(BrepCoedge? Coedge, double Turn)>> loops, List<BrepEdge> edges)
    {
        if (s.Kind == SurfaceKind.Plane) return [.. loops.Select((l, i) => new BrepLoop([.. l.Select(c => c.Coedge!.Value)], Outer: i == 0))];
        var images = new Dictionary<int, UvCurve>();
        (double U, double V)? anchor = null;
        var outer = (U: 0.0, V: 0.0);
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
            // A hole moved by whole turns to lie across the middle of the outer loop's span: started from its first
            // point, one half a turn round from it can come out a turn off.
            if (index > 0 && coedges.Count > 0)
            {
                var (u, v) = (Middle(coedges, p => p.U), Middle(coedges, p => p.V));
                var (du, dv) = (SurfaceParameters.Turns(outer.U - u), SurfaceParameters.PeriodicV(s) ? SurfaceParameters.Turns(outer.V - v) : 0);
                if (du != 0 || dv != 0) coedges = [.. coedges.Select(c => c with { Uv = c.Uv!.Shifted(du, dv) })];
            }
            if (index == 0) outer = (Middle(coedges, p => p.U), Middle(coedges, p => p.V));
            result.Add(new BrepLoop(coedges, Outer: index == 0));
        }
        return result;

        static double Middle(List<BrepCoedge> coedges, Func<(double U, double V), double> along)
        {
            var values = coedges.SelectMany(c => c.Uv!.Poles).Select(along).ToList();
            return (values.Min() + values.Max()) / 2;
        }
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

    internal static IsoSurface Surface(Surface s, int id, string source) => s switch
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
