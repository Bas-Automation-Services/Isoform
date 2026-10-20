using BasAutomation.Geometry;
using Isoform.Geometry;
using Isoform.Meshing;

namespace Isoform.Modeling;

/// <summary>Inertia tensor I = ∫ (|r|² 1 − r rᵀ) dm: moments on the diagonal, products off it (Xy = −∫ x y dm).</summary>
public readonly record struct InertiaTensor(double Xx, double Yy, double Zz, double Xy, double Yz, double Zx);

/// <summary>Volume, surface area, mass, centroid and inertia about the centroid of a solid.</summary>
public sealed record MassProperties(double Volume, double Area, double Mass, Point3D Centroid, InertiaTensor Inertia)
{
    /// <summary>
    /// Integrates over the closed mesh with the divergence theorem (Eberly's polyhedral mass properties), then corrects
    /// each triangle for the curved surface above it. Mesh vertices lie on the exact surface, so the gap h between a
    /// triangle and its surface vanishes at the corners; for a quadratic gap the edge-midpoint rule ∫h = A/3·Σh(mᵢ) is
    /// exact, and h(m) = −f(m) because the field is the exact distance near a face. The area gets the same rule for
    /// 1/cos θ. Flat faces are exact; on curved ones the chord error (~ChordTolerance) drops to higher order. A midpoint
    /// where another surface decides the field (a chord across a rim) counts as no gap; the volume there is the curved
    /// face's correction, and the area loses the circular segment the flat triangle covers beyond the rim. An estimate,
    /// not a bound: see <see cref="VolumeBounds"/>.
    /// </summary>
    public static MassProperties Of(Field field, Mesh mesh, double density = 1)
    {
        var v = mesh.Vertices;
        var o = v.Count > 0 ? v[0] : new Point3D(0, 0, 0); // integrate about a vertex: smaller numbers, less cancellation
        var s = new double[10];
        var c = new double[10];
        var area = 0.0;
        foreach (var t in mesh.Triangles())
        {
            Vector3D a = v[t.A] - o, b = v[t.B] - o, d = v[t.C] - o;
            var n = (b - a).Cross(d - a);
            var (f1x, f2x, f3x, g0x, g1x, g2x) = Terms(a.X, b.X, d.X);
            var (f1y, f2y, f3y, g0y, g1y, g2y) = Terms(a.Y, b.Y, d.Y);
            var (f1z, f2z, f3z, g0z, g1z, g2z) = Terms(a.Z, b.Z, d.Z);
            s[0] += n.X * f1x;
            s[1] += n.X * f2x; s[2] += n.Y * f2y; s[3] += n.Z * f2z;
            s[4] += n.X * f3x; s[5] += n.Y * f3y; s[6] += n.Z * f3z;
            s[7] += n.X * (a.Y * g0x + b.Y * g1x + d.Y * g2x);
            s[8] += n.Y * (a.Z * g0y + b.Z * g1y + d.Z * g2y);
            s[9] += n.Z * (a.X * g0z + b.X * g1z + d.X * g2z);

            var triangle = n.Length / 2;
            if (triangle == 0) continue;
            var normal = n / (2 * triangle);
            double gap = 0, slant = 0, rim = 0;
            foreach (var (p, q) in new[] { (a, b), (b, d), (d, a) })
            {
                var m = o + (p + q) / 2;
                var here = field.Sample(m);
                // The triangle's own surface must decide the field there; a twin surface of the same shape may tie it.
                if (field.SampleSurface(t.Owner, m) is not { } sample || sample.Value != here.Value)
                {
                    // A chord across a curved rim in the triangle's plane: the triangle covers a segment of about
                    // 2/3 · chord · sagitta beyond it (or falls short by one).
                    if (Math.Abs(normal.Dot(here.Gradient)) < 0.5) rim -= 2.0 / 3 * (q - p).Length * here.Value;
                    continue;
                }
                var cos = normal.Dot(sample.Gradient) / sample.Gradient.Length;
                if (cos < 0.5) continue;
                gap -= sample.Value / cos;
                slant += 1 / cos - 1;
            }
            area += triangle * (1 + slant / 3) + rim;
            // The sliver's volume, placed at the triangle's centroid.
            var dv = triangle * gap / 3;
            var g = (a + b + d) / 3;
            c[0] += dv;
            c[1] += dv * g.X; c[2] += dv * g.Y; c[3] += dv * g.Z;
            c[4] += dv * g.X * g.X; c[5] += dv * g.Y * g.Y; c[6] += dv * g.Z * g.Z;
            c[7] += dv * g.X * g.Y; c[8] += dv * g.Y * g.Z; c[9] += dv * g.Z * g.X;
        }
        double[] scale = [1.0 / 6, 1.0 / 24, 1.0 / 24, 1.0 / 24, 1.0 / 60, 1.0 / 60, 1.0 / 60, 1.0 / 120, 1.0 / 120, 1.0 / 120];
        var i = new double[10];
        for (var k = 0; k < 10; k++) i[k] = s[k] * scale[k] + c[k];

        var volume = i[0];
        var (x, y, z) = (i[1] / volume, i[2] / volume, i[3] / volume);
        var inertia = new InertiaTensor(
            density * (i[5] + i[6] - volume * (y * y + z * z)),
            density * (i[4] + i[6] - volume * (z * z + x * x)),
            density * (i[4] + i[5] - volume * (x * x + y * y)),
            -density * (i[7] - volume * x * y),
            -density * (i[8] - volume * y * z),
            -density * (i[9] - volume * z * x));
        return new(volume, area, density * volume, new Point3D(o.X + x, o.Y + y, o.Z + z), inertia);
    }

    /// <summary>
    /// A certified enclosure of the volume: an interval-pruned octree over the box down to <paramref name="cellSize"/>.
    /// Cells the bounds prove inside count fully, cells proved outside not at all. A cell holding one plane counts exactly
    /// (the box cut by the plane). A finest cell holding one curved patch of curvature radius ≥ R is sandwiched between two
    /// planes parallel to its tangent plane, 2δ apart with δ = R − √(R² − D²), D the reach from the tangent point to the
    /// cell's corners. Any other finest cell counts as [0, its volume]. Certified up to floating-point rounding; the width
    /// shrinks with the edges' length × cellSize² plus the curved area × cellSize².
    /// </summary>
    public static Interval VolumeBounds(Field field, Point3D min, Point3D max, double cellSize)
    {
        var m = new Vector3D(cellSize, cellSize, cellSize);
        return Cell(field, new Box3D(min - m, max + m), cellSize, 0);
    }

    static Interval Cell(Field field, Box3D box, double cellSize, int depth)
    {
        var bounds = field.Bounds(box);
        if (bounds.Lo > 0) return new(0, 0);
        var volume = BoxVolume(box);
        if (bounds.Hi < 0) return new(volume, volume);
        var f = field.Simplify(box);
        var e = box.Max - box.Min;
        var finest = Math.Max(e.X, Math.Max(e.Y, e.Z)) <= cellSize;
        if (f.SinglePatch(box) is { } r && (finest || double.IsPositiveInfinity(r)) && Sandwich(f, box, r) is { } s) return s;
        if (finest) return new(0, volume);

        var c = box.Center;
        var parts = new Interval[8];
        void Child(int i) => parts[i] = Cell(f, new Box3D(
            new Point3D((i & 1) == 0 ? box.Min.X : c.X, (i & 2) == 0 ? box.Min.Y : c.Y, (i & 4) == 0 ? box.Min.Z : c.Z),
            new Point3D((i & 1) == 0 ? c.X : box.Max.X, (i & 2) == 0 ? c.Y : box.Max.Y, (i & 4) == 0 ? c.Z : box.Max.Z)), cellSize, depth + 1);
        if (depth < 3) Parallel.For(0, 8, Child);
        else for (var i = 0; i < 8; i++) Child(i);
        double lo = 0, hi = 0;
        foreach (var p in parts) (lo, hi) = (lo + p.Lo, hi + p.Hi);
        return new(lo, hi);
    }

    /// <summary>
    /// Bounds on the material in a cell whose surface is one patch of curvature radius ≥ r. Within D ≤ r/2 of a surface
    /// point q such a patch is a single sheet within r − √(r² − D²) of its tangent plane at q.
    /// </summary>
    static Interval? Sandwich(Field f, Box3D box, double r)
    {
        var q = box.Center;
        for (var it = 0; it < 6; it++)
        {
            var s = f.Sample(q);
            q -= s.Gradient * (s.Value / s.Gradient.LengthSquared);
        }
        var at = f.Sample(q);
        var n = at.Gradient.Normalized();
        var reach = 0.0;
        foreach (var x in new[] { box.Min.X, box.Max.X })
        foreach (var y in new[] { box.Min.Y, box.Max.Y })
        foreach (var z in new[] { box.Min.Z, box.Max.Z })
            reach = Math.Max(reach, (new Point3D(x, y, z) - q).Length);
        var delta = Math.Abs(at.Value) + 1e-12;
        if (!double.IsPositiveInfinity(r))
        {
            if (reach > r / 2) return null;
            delta += r - Math.Sqrt(r * r - reach * reach);
        }
        var level = n.Dot(q.AsVector());
        return new(HalfSpaceVolume(box, n, level - delta), HalfSpaceVolume(box, n, level + delta));
    }

    /// <summary>
    /// Volume of the part of the box where n·x ≤ level: mirror the axes so n ≥ 0, then inclusion–exclusion over the box
    /// corners, Σ (−1)^|v| (level − n·v)₊^m / (m! Π nᵢ). Components below 1e-9 are dropped; the cell is a prism along them.
    /// </summary>
    static double HalfSpaceVolume(Box3D box, Vector3D n, double level)
    {
        double[] size = [box.Max.X - box.Min.X, box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z];
        double[] k = [n.X, n.Y, n.Z];
        level -= n.Dot(box.Min.AsVector());
        Span<double> edge = stackalloc double[3];
        var (m, prism, product, total) = (0, 1.0, 1.0, 0.0);
        for (var i = 0; i < 3; i++)
        {
            if (k[i] < 0)
            {
                level -= k[i] * size[i];
                k[i] = -k[i];
            }
            if (k[i] < 1e-9) prism *= size[i];
            else
            {
                edge[m++] = k[i] * size[i];
                product *= k[i];
                total += k[i] * size[i];
            }
        }
        if (level <= 0) return 0;
        if (level >= total) return size[0] * size[1] * size[2];
        var sum = 0.0;
        for (var corner = 0; corner < 1 << m; corner++)
        {
            var (x, sign) = (level, 1);
            for (var j = 0; j < m; j++)
                if ((corner >> j & 1) == 1) (x, sign) = (x - edge[j], -sign);
            if (x > 0) sum += sign * Math.Pow(x, m);
        }
        return sum / ((m == 3 ? 6 : m) * product) * prism;
    }

    static double BoxVolume(Box3D b) => (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y) * (b.Max.Z - b.Min.Z);

    // Eberly's subexpressions for one coordinate of a triangle.
    static (double F1, double F2, double F3, double G0, double G1, double G2) Terms(double w0, double w1, double w2)
    {
        var t0 = w0 + w1;
        var f1 = t0 + w2;
        var t1 = w0 * w0;
        var t2 = t1 + w1 * t0;
        var f2 = t2 + w2 * f1;
        var f3 = w0 * t1 + w1 * t2 + w2 * f2;
        return (f1, f2, f3, f2 + w0 * (f1 + w0), f2 + w1 * (f1 + w1), f2 + w2 * (f1 + w2));
    }
}
