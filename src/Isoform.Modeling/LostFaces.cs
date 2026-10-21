using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Modeling;

/// <summary>
/// Faces the cells lost. A surface no polygon carries can still bound the part over a patch thinner than a cell. On the
/// path from the root to the surface's leaf every intersection and union is decided by the leaf while each sibling
/// stays clear of it: below in an intersection, above in a union. The least of those gaps at a point of the surface,
/// its margin, is the radius of a disc round the point where the part's boundary is that surface (every field is
/// 1-Lipschitz). Branch-and-bound on interval bounds of the margin finds a point with a margin above the tolerance (a
/// face the mesh should have), proves there is none (a feature that changes the part by less than the tolerance), or
/// runs out of budget.
/// </summary>
static class LostFaces
{
    public enum Verdict { None, Face, Unknown }

    public readonly record struct Result(Verdict Verdict, double Margin = 0, Point3D At = default, int By = -1);

    /// <summary>
    /// A sibling on the path and how far it is from deciding: Sign · value + Shift. In an intersection that is the
    /// path's value less the sibling's, in a union the reverse; the path's value is zero where the surface decides,
    /// −t inside a shell's cavity term.
    /// </summary>
    public readonly record struct Gap(Field Field, double Sign, double Shift)
    {
        public double At(Point3D p) => Sign * Field.Value(p) + Shift;

        public double Upper(Box3D b) => Field.Bounds(b) is var v && Sign < 0 ? Shift - v.Lo : v.Hi + Shift;
    }

    /// <summary>Where a surface sits in a body.</summary>
    public sealed class Path(Field? end, List<Gap> siblings, double level)
    {
        /// <summary>The leaf, or a node other than ∩, ∪ and ¬ holding it (then no margin is known); null if found twice.</summary>
        public readonly Field? End = end;
        public readonly List<Gap> Siblings = siblings;

        /// <summary>The value at <see cref="End"/> where the surface decides.</summary>
        public readonly double Level = level;
    }

    /// <summary>The surface's place in the body, or null if it is not in it.</summary>
    public static Path? Find(Field body, int id)
    {
        var found = new List<Path>();
        Find(body, id, 0, [], found);
        // In the body twice (a face a fillet holds): nothing can be decided.
        return found.Count == 0 ? null : found.Count == 1 ? found[0] : new Path(null, [], 0);
    }

    static void Find(Field f, int id, double level, List<Gap> siblings, List<Path> found)
    {
        switch (f)
        {
            case LeafField l:
                if (l.Surface.Id == id) found.Add(new(l, [.. siblings], level));
                break;
            case NegateField n:
                Find(n.Child, id, -level, siblings, found);
                break;
            case MaxField or MinField:
                var (children, sign) = f is MaxField mx ? (mx.Children, -1.0) : (((MinField)f).Children, 1.0);
                for (var k = 0; k < children.Count; k++)
                {
                    var before = siblings.Count;
                    for (var j = 0; j < children.Count; j++)
                        if (j != k) siblings.Add(new(children[j], sign, -sign * level));
                    Find(children[k], id, level, siblings, found);
                    siblings.RemoveRange(before, siblings.Count - before);
                }
                break;
            case ShellField sh when level == 0:
                // max(outer, −child − t): a cavity face is the offset of a child's face, deciding where child = −t.
                var outer = sh.InnerOf.Where(kv => kv.Value == id).Select(kv => kv.Key).DefaultIfEmpty(-1).First();
                siblings.Add(outer >= 0 ? new(sh.Outer, -1, 0) : new(sh.Child, 1, sh.Thickness));
                if (outer >= 0) Find(sh.Child, outer, -sh.Thickness, siblings, found);
                else Find(sh.Outer, id, 0, siblings, found);
                siblings.RemoveAt(siblings.Count - 1);
                break;
            default:
                if (f.SampleSurface(id, default) is not null) found.Add(new(f, [.. siblings], level));
                break;
        }
    }

    public static Result Search(Path path, Surface s, Box3D box, double tolerance, int budget = 200)
    {
        if (path.End is not { } end) return new(Verdict.Unknown);
        var witness = end is LeafField;
        var surface = new LeafField(s);

        // A plane along an axis is searched in its own plane: bounds over a flat box are exact there.
        if (s.Kind == SurfaceKind.Plane && Axis(s.Axis) is { } axis)
        {
            var at = At(s.Origin, axis);
            if (at < At(box.Min, axis) || at > At(box.Max, axis)) return new(Verdict.None);
            box = new Box3D(With(box.Min, axis, at), With(box.Max, axis, at));
        }
        // An axis-aligned cylinder or a sphere is searched only within its own extent.
        if (s.Kind is SurfaceKind.Cylinder or SurfaceKind.Sphere && (s.Kind == SurfaceKind.Sphere ? -1 : Axis(s.Axis)) is { } along)
        {
            var (lo, hi) = (box.Min, box.Max);
            for (var k = 0; k < 3; k++)
                if (k != along) (lo, hi) = (With(lo, k, Math.Max(At(lo, k), At(s.Origin, k) - s.Radius)), With(hi, k, Math.Min(At(hi, k), At(s.Origin, k) + s.Radius)));
            if (lo.X > hi.X || lo.Y > hi.Y || lo.Z > hi.Z) return new(Verdict.None);
            box = new Box3D(lo, hi);
        }

        // Siblings that are planes in the box (simplified there): one parallel to the surface is a constant gap on it,
        // and two whose gaps slope in opposite directions along it (a face flush with another, or a plane through
        // their common edge) have a weighted sum that is constant there, which caps the margin at its average.
        // A sibling ∩ in an intersection, or ∪ in a union, is a gap per child: so planes nested below count too.
        var n = s.Kind == SurfaceKind.Plane ? s.Axis : (Vector3D?)null;
        var linear = new List<(Vector3D Along, double Gap)>();
        double Upper(Box3D b)
        {
            if (!Contains(surface.Bounds(b), 0) || !Contains(end.Bounds(b), path.Level)) return double.NegativeInfinity;
            var u = double.PositiveInfinity;
            linear.Clear();
            bool Add(Field f, double sign, double shift)
            {
                switch (f)
                {
                    case MaxField x when sign < 0:
                        foreach (var c in x.Children) if (Add(c, sign, shift)) return true;
                        return false;
                    case MinField x when sign > 0:
                        foreach (var c in x.Children) if (Add(c, sign, shift)) return true;
                        return false;
                    case NegateField x:
                        return Add(x.Child, -sign, shift);
                    // A rounded edge only removes material: in an intersection its gap is at most either plane's, so
                    // the planes are upper bounds too, and can meet another plane in the caps below.
                    case RoundedEdgeField x when sign < 0:
                        if ((u = Math.Min(u, new Gap(f, sign, shift).Upper(b))) <= tolerance) return true;
                        return Add(x.A, sign, shift) || Add(x.B, sign, shift);
                }
                if ((u = Math.Min(u, new Gap(f, sign, shift).Upper(b))) <= tolerance) return true;
                if (f is not LeafField { Surface.Kind: SurfaceKind.Plane } l) return false;
                var a = l.Surface.Axis * (sign * (l.Complement ? -1 : 1));
                var along = n is { } m ? a - m * a.Dot(m) : a;
                var gap = sign * l.Value(s.Origin) + shift; // on the surface: its origin when it is a plane
                if (n is not null && along.Length < 1e-12) u = Math.Min(u, gap);
                for (var i = 0; i < linear.Count; i++)
                {
                    var (other, at) = linear[i];
                    var (wa, wb) = (other.Length, along.Length);
                    // wa·along + wb·other = 0: wa·gap + wb·at is constant on the plane.
                    if (along.Dot(other) < 0 && (along * wa + other * wb).Length <= 1e-9 * wa * wb) u = Math.Min(u, (wa * gap + wb * at) / (wa + wb));
                    // Three planes round a point of the surface plane (a cut through a vertex): with w the in-plane
                    // cross products of the other two, Σ w·along = 0 for any three vectors in a plane, and when the
                    // weights share a sign their weighted average gap is constant on it.
                    if (n is not { } m3) continue;
                    for (var j = i + 1; j < linear.Count; j++)
                    {
                        var (third, at3) = linear[j];
                        var (w1, w2, w3) = (other.Cross(third).Dot(m3), third.Cross(along).Dot(m3), along.Cross(other).Dot(m3));
                        if (w1 < 0) (w1, w2, w3) = (-w1, -w2, -w3);
                        if (w1 > 1e-12 && w2 > 1e-12 && w3 > 1e-12) u = Math.Min(u, (w1 * gap + w2 * at + w3 * at3) / (w1 + w2 + w3));
                    }
                }
                linear.Add((along, gap));
                return u <= tolerance;
            }
            foreach (var g in path.Siblings)
                if (Add(g.Field.Simplify(b), g.Sign, g.Shift)) return u;
            return u;
        }

        var queue = new PriorityQueue<Box3D, double>();
        queue.Enqueue(box, -Upper(box));
        while (queue.TryDequeue(out var b, out var negative))
        {
            if (-negative <= tolerance) break;
            if (budget-- <= 0) return new(Verdict.Unknown);
            if (witness && Ascend(path, s, b) is var w && w.Margin > tolerance) return w;
            foreach (var child in Split(b))
            {
                var u = Upper(child);
                if (u > tolerance) queue.Enqueue(child, -u);
            }
        }
        return new(Verdict.None);
    }

    static bool Contains(Interval v, double x) => v.Lo <= x && v.Hi >= x;

    /// <summary>The margin at a point of the surface and the sibling closest to deciding there.</summary>
    static (double Margin, int By) Margin(Path path, Point3D p)
    {
        var (m, by) = (double.PositiveInfinity, -1);
        foreach (var g in path.Siblings)
        {
            var x = g.Field.Sample(p);
            if (g.Sign * x.Value + g.Shift < m) (m, by) = (g.Sign * x.Value + g.Shift, x.Owner);
        }
        return (m, by);
    }

    /// <summary>
    /// From the box's centre on the surface, a few steps up the margin: the gaps linearised in the tangent plane, their
    /// lowest maximised exactly (a 2D linear programme), then back onto the surface. Reaches the middle of a strip of
    /// any width at once; on a curved surface the step shrinks until it gains.
    /// </summary>
    static Result Ascend(Path path, Surface s, Box3D box)
    {
        var at = Onto(s, box.Center);
        var reach = (box.Max - box.Min).Length;
        if (s.Kind is SurfaceKind.Cylinder or SurfaceKind.Sphere or SurfaceKind.Torus) reach = Math.Min(reach, (s.Kind == SurfaceKind.Torus ? s.Minor : s.Radius) / 4);
        var (best, by) = Margin(path, at);
        for (var it = 0; it < 12; it++)
        {
            var p = at;
            var normal = s.Gradient(p).Normalized();
            var u = (Math.Abs(normal.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0)).Cross(normal).Normalized();
            var v = normal.Cross(u);
            var gaps = path.Siblings.Select(x =>
            {
                var sample = x.Field.Sample(p);
                var g = sample.Gradient * x.Sign;
                return (C: x.Sign * sample.Value + x.Shift, U: g.Dot(u), V: g.Dot(v));
            }).OrderBy(x => x.C).Take(10).ToArray();
            var (du, dv) = Highest(gaps, reach);
            p = Onto(s, p + u * du + v * dv);
            var (m, b) = Margin(path, p);
            if (m > best) (best, by, at) = (m, b, p);
            else if (s.Kind == SurfaceKind.Plane) break; // exact already, unless the active pieces changed
            else reach /= 2;
        }
        return new(Verdict.Face, best, at, by);
    }

    /// <summary>The point of the square |x|, |y| ≤ r where the lowest of the planes c + u·x + v·y is highest.</summary>
    static (double, double) Highest((double C, double U, double V)[] g, double r)
    {
        double Low(double x, double y) => g.Min(k => k.C + k.U * x + k.V * y);
        var (best, bx, by) = (double.NegativeInfinity, 0.0, 0.0);
        void Try(double x, double y)
        {
            if (!(Math.Abs(x) <= r * (1 + 1e-12) && Math.Abs(y) <= r * (1 + 1e-12))) return;
            var l = Low(x, y);
            if (l > best) (best, bx, by) = (l, x, y);
        }
        // Optimum at a vertex of the arrangement: three planes equal, two on a side of the square, or a corner.
        foreach (var x in new[] { -r, r })
            foreach (var y in new[] { -r, r }) Try(x, y);
        for (var i = 0; i < g.Length; i++)
            for (var j = i + 1; j < g.Length; j++)
            {
                var (dc, du, dv) = (g[j].C - g[i].C, g[i].U - g[j].U, g[i].V - g[j].V);
                foreach (var side in new[] { -r, r })
                {
                    if (Math.Abs(dv) > 1e-15) Try(side, (dc - du * side) / dv);
                    if (Math.Abs(du) > 1e-15) Try((dc - dv * side) / du, side);
                }
                for (var k = j + 1; k < g.Length; k++)
                {
                    var (ec, eu, ev) = (g[k].C - g[i].C, g[i].U - g[k].U, g[i].V - g[k].V);
                    var det = du * ev - dv * eu;
                    if (Math.Abs(det) > 1e-15) Try((dc * ev - dv * ec) / det, (du * ec - dc * eu) / det);
                }
            }
        return (bx, by);
    }

    static Point3D Onto(Surface s, Point3D p)
    {
        for (var it = 0; it < (s.Kind == SurfaceKind.Plane ? 1 : 6); it++) p -= s.Gradient(p) * s.Distance(p);
        return p;
    }

    /// <summary>Halves the box along every side it has.</summary>
    internal static IEnumerable<Box3D> Split(Box3D b)
    {
        var c = b.Center;
        var (sx, sy, sz) = (b.Max.X > b.Min.X ? 2 : 1, b.Max.Y > b.Min.Y ? 2 : 1, b.Max.Z > b.Min.Z ? 2 : 1);
        for (var i = 0; i < sx; i++)
        for (var j = 0; j < sy; j++)
        for (var k = 0; k < sz; k++)
            yield return new Box3D(
                new Point3D(sx == 1 ? b.Min.X : i == 0 ? b.Min.X : c.X, sy == 1 ? b.Min.Y : j == 0 ? b.Min.Y : c.Y, sz == 1 ? b.Min.Z : k == 0 ? b.Min.Z : c.Z),
                new Point3D(sx == 1 ? b.Max.X : i == 0 ? c.X : b.Max.X, sy == 1 ? b.Max.Y : j == 0 ? c.Y : b.Max.Y, sz == 1 ? b.Max.Z : k == 0 ? c.Z : b.Max.Z));
    }

    internal static int? Axis(Vector3D a) => a.Y == 0 && a.Z == 0 ? 0 : a.X == 0 && a.Z == 0 ? 1 : a.X == 0 && a.Y == 0 ? 2 : null;

    internal static double At(Point3D p, int axis) => axis switch { 0 => p.X, 1 => p.Y, _ => p.Z };

    internal static Point3D With(Point3D p, int axis, double v) => axis switch { 0 => new(v, p.Y, p.Z), 1 => new(p.X, v, p.Z), _ => new(p.X, p.Y, v) };
}
