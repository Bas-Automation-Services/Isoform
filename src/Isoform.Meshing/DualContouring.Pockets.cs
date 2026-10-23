using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Meshing;

public static partial class DualContouring
{
    // Boxes a pocket search halves at most per cell.
    const int PocketBudget = 32;

    /// <summary>
    /// The deepest point found in a leaf without a vertex where the field has the other sign than at its corners:
    /// material (or a gap) the cells lose whole, as a sliver body at a clipped corner. Best-first branch-and-bound on
    /// the leaf's field, which is exact in its box, until the depth found is half the bound or the budget is spent;
    /// null where none is found.
    /// </summary>
    static Pocket? FindPocket(Node leaf, Grid grid)
    {
        var field = leaf.Field!;
        var box = grid.Box(leaf);
        var sign = field.Value(box.Min) < 0 ? 1.0 : -1.0; // the depth of the other sign is sign · value
        double Upper(Box3D b) => Tight(field, b) is var v && sign > 0 ? v.Hi : -v.Lo;
        var (best, at) = (0.0, default(Point3D));
        var queue = new PriorityQueue<Box3D, double>();
        queue.Enqueue(box, -Upper(box));
        for (var budget = PocketBudget; budget > 0 && queue.TryDequeue(out var b, out var negative); budget--)
        {
            if (-negative <= Math.Max(2 * best, CertifyMargin)) break;
            var c = b.Center;
            var sample = field.Sample(c);
            if (sign * sample.Value is var d && d > best) (best, at) = (d, c);
            // And a Newton step from there to half the bound: the deciding surface's other side.
            var q = c + sample.Gradient * (sign * (-negative / 2 - d) / sample.Gradient.LengthSquared);
            if (Inside(b, q, 0) && sign * field.Value(q) is var e && e > best) (best, at) = (e, q);
            // Halve the side whose halves bound the depth least in sum, the longest of equals: a sliver along an axis
            // is then searched across it only.
            var (pick, low, high, ul, uh, sum) = (-1, b, b, 0.0, 0.0, 0.0);
            for (var axis = 0; axis < 3; axis++)
            {
                var (l, h) = Halves(b, axis);
                var (u1, u2) = (Upper(l), Upper(h));
                var s = Math.Max(u1, 0) + Math.Max(u2, 0);
                if (pick < 0 || s < sum || s == sum && Side(b, axis) > Side(b, pick)) (pick, low, high, ul, uh, sum) = (axis, l, h, u1, u2, s);
            }
            var floor = Math.Max(2 * best, CertifyMargin);
            if (ul > floor) queue.Enqueue(low, -ul);
            if (uh > floor) queue.Enqueue(high, -uh);
        }
        if (best <= 0) return null;
        var corner = double.PositiveInfinity;
        for (var k = 0; k < 8; k++)
            corner = Math.Min(corner, (new Point3D((k & 1) == 0 ? box.Min.X : box.Max.X, (k & 2) == 0 ? box.Min.Y : box.Max.Y, (k & 4) == 0 ? box.Min.Z : box.Max.Z) - at).Length);
        return new Pocket(at, sign * best, corner);
    }

    static (Box3D, Box3D) Halves(Box3D b, int axis)
    {
        var c = b.Center;
        return axis switch
        {
            0 => (new Box3D(b.Min, new Point3D(c.X, b.Max.Y, b.Max.Z)), new Box3D(new Point3D(c.X, b.Min.Y, b.Min.Z), b.Max)),
            1 => (new Box3D(b.Min, new Point3D(b.Max.X, c.Y, b.Max.Z)), new Box3D(new Point3D(b.Min.X, c.Y, b.Min.Z), b.Max)),
            _ => (new Box3D(b.Min, new Point3D(b.Max.X, b.Max.Y, c.Z)), new Box3D(new Point3D(b.Min.X, b.Min.Y, c.Z), b.Max)),
        };
    }

    static double Side(Box3D b, int axis) => axis switch { 0 => b.Max.X - b.Min.X, 1 => b.Max.Y - b.Min.Y, _ => b.Max.Z - b.Min.Z };

    /// <summary>
    /// The field's bounds over a box, tightened where two planes meet in one ∩ or ∪: their mean bounds the ∩ from
    /// below and the ∪ from above. Opposite planes have a constant mean, so a contact of zero thickness (a cut flush
    /// with a face, blocks face to face) is proven to hold nothing, which plain bounds never do.
    /// </summary>
    static Interval Tight(Field f, Box3D b)
    {
        switch (f)
        {
            case NegateField n:
                return Tight(n.Child, b).Negate();
            case MaxField or MinField:
                var max = f is MaxField;
                var children = max ? ((MaxField)f).Children : ((MinField)f).Children;
                var r = Tight(children[0], b);
                for (var i = 1; i < children.Count; i++) r = max ? Interval.Max(r, Tight(children[i], b)) : Interval.Min(r, Tight(children[i], b));
                for (var i = 0; i < children.Count; i++)
                for (var j = i + 1; j < children.Count; j++)
                {
                    if (children[i] is not LeafField { Surface.Kind: SurfaceKind.Plane } p || children[j] is not LeafField { Surface.Kind: SurfaceKind.Plane } q) continue;
                    var (np, nq) = (p.Surface.Axis * (p.Complement ? -1 : 1), q.Surface.Axis * (q.Complement ? -1 : 1));
                    var n = (np + nq) / 2;
                    var centre = (np.Dot(b.Center - p.Surface.Origin) + nq.Dot(b.Center - q.Surface.Origin)) / 2;
                    var spread = (Math.Abs(n.X) * (b.Max.X - b.Min.X) + Math.Abs(n.Y) * (b.Max.Y - b.Min.Y) + Math.Abs(n.Z) * (b.Max.Z - b.Min.Z)) / 2;
                    r = max ? new Interval(Math.Max(r.Lo, centre - spread), r.Hi) : new Interval(r.Lo, Math.Min(r.Hi, centre + spread));
                }
                return r;
            default:
                return f.Bounds(b);
        }
    }

    static List<Pocket> Sorted(IEnumerable<Pocket> pockets) =>
        pockets.OrderBy(p => p.Location.X).ThenBy(p => p.Location.Y).ThenBy(p => p.Location.Z).ToList();
}
