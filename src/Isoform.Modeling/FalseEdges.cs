using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Modeling;

/// <summary>
/// Edges the cells drew between faces that do not meet. Where a piece of a face is narrower than a cell, such as the
/// step between two parallel faces or a corner a cut just clears, the mesh can join the faces on either side of it
/// directly. A real edge has a point near its chain where both surfaces and the part's boundary pass: a few Newton
/// steps from the chain's vertices find it. Without one, branch-and-bound on interval bounds of the two surfaces and
/// the body proves that there is none (a false edge), finds a box too small to split, or runs out of budget.
/// </summary>
static class FalseEdges
{
    public enum Verdict { Meet, Apart, Unknown }

    /// <summary>An edge whose faces provably do not meet near it: the two surfaces and a point of its chain.</summary>
    public readonly record struct Found(int A, int B, Point3D At);

    /// <param name="reach">How far from its chain an edge may lie: the finest cell.</param>
    public static List<Found> Find(Field body, IReadOnlyList<Surface> surfaces, TopologyBuilder.Result built, double tolerance, double reach)
    {
        var found = new List<Found>();
        var topology = built.Topology;
        foreach (var edge in topology.Edges)
        {
            var (a, b) = (built.FaceOwners[edge.FaceA], built.FaceOwners[edge.FaceB]);
            if (a.Concat(b).Any(o => !surfaces[o].Analytic)) continue;
            var points = built.EdgePoints[edge.Index];
            var region = Region(points, reach);
            // Apart only if every pair of surfaces of the two faces is.
            var apart = a.All(x => b.All(y => Check(body, surfaces[x], surfaces[y], points, region, tolerance) == Verdict.Apart));
            if (apart) found.Add(new(a.Max(), b.Max(), points[points.Length / 2]));
        }
        return found;
    }

    public static Verdict Check(Field body, Surface a, Surface b, IReadOnlyList<Point3D> near, Box3D region, double tolerance, int budget = 400)
    {
        if (Disjoint(a, b, tolerance)) return Verdict.Apart;
        foreach (var p in near)
            if (Witness(body, a, b, p, tolerance)) return Verdict.Meet;

        // An axis-aligned plane is searched in its own plane: bounds over a flat box are exact there.
        foreach (var s in new[] { a, b })
            if (s.Kind == SurfaceKind.Plane && LostFaces.Axis(s.Axis) is { } axis)
            {
                var at = LostFaces.At(s.Origin, axis);
                if (at < LostFaces.At(region.Min, axis) - tolerance || at > LostFaces.At(region.Max, axis) + tolerance) return Verdict.Apart;
                region = new Box3D(LostFaces.With(region.Min, axis, at), LostFaces.With(region.Max, axis, at));
                break;
            }

        var (la, lb, field) = (new LeafField(a), new LeafField(b), body.Simplify(region));
        bool Near(Interval v) => v.Lo <= tolerance && v.Hi >= -tolerance;
        var stack = new Stack<Box3D>();
        stack.Push(region);
        while (stack.TryPop(out var box))
        {
            if (!Near(la.Bounds(box)) || !Near(lb.Bounds(box)) || !Near(field.Bounds(box))) continue;
            if (budget-- <= 0 || (box.Max - box.Min).Length <= 2 * tolerance) return Verdict.Unknown;
            if (budget % 16 == 0 && Witness(body, a, b, box.Center, tolerance)) return Verdict.Meet;
            foreach (var child in LostFaces.Split(box)) stack.Push(child);
        }
        return Verdict.Apart;
    }

    /// <summary>Parallel planes, coaxial cylinders and concentric spheres that are not the same surface never meet.</summary>
    static bool Disjoint(Surface a, Surface b, double tolerance)
    {
        if (a.Kind != b.Kind) return false;
        return a.Kind switch
        {
            SurfaceKind.Plane => a.Axis.Cross(b.Axis).Length <= 1e-12 && Math.Abs(a.Axis.Dot(b.Origin - a.Origin)) > tolerance,
            SurfaceKind.Cylinder => a.Axis.Cross(b.Axis).Length <= 1e-12 && Radial(a, b.Origin) <= 1e-12 && Math.Abs(a.Radius - b.Radius) > tolerance,
            SurfaceKind.Sphere => (a.Origin - b.Origin).Length <= 1e-12 && Math.Abs(a.Radius - b.Radius) > tolerance,
            _ => false,
        };
    }

    static double Radial(Surface s, Point3D p)
    {
        var v = p - s.Origin;
        return (v - s.Axis * v.Dot(s.Axis)).Length;
    }

    /// <summary>
    /// Newton steps from <paramref name="p"/> onto both surfaces (the least step that zeroes both linearised), then
    /// whether the point found lies on both and on the part's boundary.
    /// </summary>
    static bool Witness(Field body, Surface a, Surface b, Point3D p, double tolerance)
    {
        for (var it = 0; it < 12; it++)
        {
            var (fa, fb) = (a.Distance(p), b.Distance(p));
            if (Math.Abs(fa) <= tolerance / 10 && Math.Abs(fb) <= tolerance / 10) break;
            var (ga, gb) = (a.Gradient(p), b.Gradient(p));
            var (aa, ab, bb) = (ga.Dot(ga), ga.Dot(gb), gb.Dot(gb));
            var det = aa * bb - ab * ab;
            if (det <= 1e-12 * aa * bb)
            {
                // Tangent surfaces: onto one, then the other.
                p -= ga * (fa / aa);
                p -= gb * (b.Distance(p) / bb);
                continue;
            }
            var (la, lb) = ((bb * fa - ab * fb) / det, (aa * fb - ab * fa) / det);
            p -= ga * la + gb * lb;
        }
        return Math.Abs(a.Distance(p)) <= tolerance && Math.Abs(b.Distance(p)) <= tolerance && Math.Abs(body.Value(p)) <= tolerance;
    }

    static Box3D Region(IReadOnlyList<Point3D> points, double pad)
    {
        var (lo, hi) = (points[0], points[0]);
        foreach (var p in points)
        {
            lo = new(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z));
            hi = new(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z));
        }
        var d = new Vector3D(pad, pad, pad);
        return new Box3D(lo - d, hi + d);
    }
}
