using System.Buffers;
using System.Numerics;
using BasAutomation.Geometry;

namespace Isoform.Geometry;

public sealed partial class Tape
{
    // Points per block: every register holds a block, so a tape of n instructions works in n × 64 doubles.
    const int Block = 64;

    /// <summary>
    /// Field values at many points, given as separate x, y and z arrays: <c>values[i] = Value((x[i], y[i], z[i]))</c>,
    /// bit for bit. Instructions run over blocks of points with SIMD where the hardware has it; leaves, intersections,
    /// unions and complements are vectorised, the rest go point by point within the block.
    /// </summary>
    public void Values(ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> values)
    {
        if (y.Length != x.Length || z.Length != x.Length || values.Length < x.Length) throw new ArgumentException("x, y, z and values must have the same length.");
        if (!Vector.IsHardwareAccelerated || _sampled is not null)
        {
            for (var i = 0; i < x.Length; i++) values[i] = Value(new Point3D(x[i], y[i], z[i]));
            return;
        }
        var n = _code.Length;
        var regs = ArrayPool<double>.Shared.Rent(n * Block);
        Span<double> pad = stackalloc double[3 * Block];
        for (var start = 0; start < x.Length; start += Block)
        {
            var m = Math.Min(Block, x.Length - start);
            ReadOnlySpan<double> bx = pad[..Block], by = pad.Slice(Block, Block), bz = pad.Slice(2 * Block, Block);
            if (m == Block)
            {
                bx = x.Slice(start, Block);
                by = y.Slice(start, Block);
                bz = z.Slice(start, Block);
            }
            else
            {
                // The last block is padded; the padding lanes are computed and ignored.
                pad.Clear();
                x.Slice(start, m).CopyTo(pad);
                y.Slice(start, m).CopyTo(pad[Block..]);
                z.Slice(start, m).CopyTo(pad[(2 * Block)..]);
            }
            for (var i = 0; i < n; i++) Lanes(i, regs, bx, by, bz, m);
            regs.AsSpan((n - 1) * Block, m).CopyTo(values.Slice(start, m));
        }
        ArrayPool<double>.Shared.Return(regs);
    }

    void Lanes(int i, double[] regs, ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, int m)
    {
        var ins = _code[i];
        var d = regs.AsSpan(i * Block, Block);
        switch (ins.Op)
        {
            case TapeOp.Plane:
                PlaneLanes(in _leaves[ins.Data], x, y, z, d);
                return;
            case TapeOp.Sphere:
            case TapeOp.Cylinder:
            case TapeOp.Cone:
            case TapeOp.Torus:
                RadialLanes(in _leaves[ins.Data], ins.Op, x, y, z, d);
                return;
            case TapeOp.Max:
            case TapeOp.Min:
                for (var j = 0; j < Block; j += Vector<double>.Count)
                {
                    var best = new Vector<double>(regs, _args[ins.A] * Block + j);
                    for (var k = 1; k < ins.B; k++)
                    {
                        var c = new Vector<double>(regs, _args[ins.A + k] * Block + j);
                        // The same strict comparison as the tree: the first of equal values wins.
                        best = Vector.ConditionalSelect(ins.Op == TapeOp.Max ? Vector.GreaterThan(c, best) : Vector.LessThan(c, best), c, best);
                    }
                    best.CopyTo(d[j..]);
                }
                return;
            case TapeOp.Rounded when RoundedLanes(ins, regs, x, y, z, d, m):
                return;
            case TapeOp.Negate:
                var minus = new Vector<double>(-1.0);
                for (var j = 0; j < Block; j += Vector<double>.Count)
                    (new Vector<double>(regs, ins.A * Block + j) * minus).CopyTo(d[j..]);
                return;
        }
        for (var j = 0; j < m; j++)
        {
            var (a, b) = (regs[ins.A * Block + j], regs[ins.B * Block + j]);
            d[j] = ins.Op switch
            {
                TapeOp.Rounded => RoundedValue(ins, a, b, new Point3D(x[j], y[j], z[j])),
                TapeOp.Shell => ShellValue(ins, a, b),
                TapeOp.Blend => BlendValue(_blends[ins.Data], a, b),
                TapeOp.Profile => ProfileValue(_profiles[ins.Data], new Point3D(x[j], y[j], z[j])),
                _ => _objects[ins.Data].Value(new Point3D(x[j], y[j], z[j])),
            };
        }
    }

    /// <summary>
    /// RoundedValue on a block, for edges between planes and cylinders. Lanes on a cylinder's axis, where the surface
    /// picks a gradient of its own, are redone one by one. False for other surfaces: the caller goes point by point.
    /// </summary>
    bool RoundedLanes(TapeInstr ins, double[] regs, ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> d, int m)
    {
        var (opA, opB) = (_code[ins.A].Op, _code[ins.B].Op);
        if (opA is not (TapeOp.Plane or TapeOp.Cylinder) || opB is not (TapeOp.Plane or TapeOp.Cylinder)) return false;
        ref readonly var la = ref _leaves[_code[ins.A].Data];
        ref readonly var lb = ref _leaves[_code[ins.B].Data];
        var r = _rounds[ins.Data];
        var (radius, minusRadius, tiny) = (new Vector<double>(r.Radius), new Vector<double>(-r.Radius), new Vector<double>(1e-12));
        for (var j = 0; j < Block; j += Vector<double>.Count)
        {
            var (px, py, pz) = (new Vector<double>(x[j..]), new Vector<double>(y[j..]), new Vector<double>(z[j..]));
            var (va, vb) = (new Vector<double>(regs, ins.A * Block + j), new Vector<double>(regs, ins.B * Block + j));
            var ok = Gradient(in la, opA, px, py, pz, out var ax, out var ay, out var az) & Gradient(in lb, opB, px, py, pz, out var bx, out var by, out var bz);
            var sharp = Vector.ConditionalSelect(Vector.GreaterThanOrEqual(va, vb), va, vb);
            var c = ax * bx + ay * by + az * bz;
            var denom = Vector<double>.One - c * c;
            var (a, b) = (va + radius, vb + radius);
            var alpha = (a - b * c) / denom;
            var beta = (b - a * c) / denom;
            var (wx, wy, wz) = (ax * alpha + bx * beta, ay * alpha + by * beta, az * alpha + bz * beta);
            var len = Vector.SquareRoot(wx * wx + wy * wy + wz * wz);
            var rounded = Vector.ConditionalSelect(Vector.GreaterThan(len, Vector<double>.Zero), len - radius, minusRadius);
            var useSharp = Vector.LessThan(denom, tiny) | Vector.LessThan(alpha, Vector<double>.Zero) | Vector.LessThan(beta, Vector<double>.Zero);
            Vector.ConditionalSelect(useSharp, sharp, rounded).CopyTo(d[j..]);
            if (ok == Vector<long>.AllBitsSet) continue;
            for (var k = 0; k < Vector<double>.Count && j + k < m; k++)
                if (ok[k] == 0) d[j + k] = RoundedValue(ins, va[k], vb[k], new Point3D(x[j + k], y[j + k], z[j + k]));
        }
        return true;
    }

    /// <summary>A plane's or cylinder's gradient, as LeafSample has it; the mask is clear on a cylinder's axis.</summary>
    static Vector<long> Gradient(in LeafData l, TapeOp op, Vector<double> x, Vector<double> y, Vector<double> z,
        out Vector<double> gx, out Vector<double> gy, out Vector<double> gz)
    {
        var sign = Sign(in l);
        if (op == TapeOp.Plane)
        {
            (gx, gy, gz) = (new Vector<double>(l.Ax) * sign, new Vector<double>(l.Ay) * sign, new Vector<double>(l.Az) * sign);
            return Vector<long>.AllBitsSet;
        }
        var (ax, ay, az) = (new Vector<double>(l.Ax), new Vector<double>(l.Ay), new Vector<double>(l.Az));
        var (vx, vy, vz) = (x - new Vector<double>(l.Ox), y - new Vector<double>(l.Oy), z - new Vector<double>(l.Oz));
        var axial = vx * ax + vy * ay + vz * az;
        var (rx, ry, rz) = (vx - ax * axial, vy - ay * axial, vz - az * axial);
        var len = Vector.SquareRoot(rx * rx + ry * ry + rz * rz);
        (gx, gy, gz) = (rx / len * sign, ry / len * sign, rz / len * sign);
        return Vector.GreaterThan(len, Vector<double>.Zero);
    }

    // Multiplying by ±1 is exact and flips the sign of zero as negation does.
    static Vector<double> Sign(in LeafData l) => new(l.Complement ? -1.0 : 1.0);

    static void PlaneLanes(in LeafData l, ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> d)
    {
        var (ox, oy, oz) = (new Vector<double>(l.Ox), new Vector<double>(l.Oy), new Vector<double>(l.Oz));
        var (ax, ay, az) = (new Vector<double>(l.Ax), new Vector<double>(l.Ay), new Vector<double>(l.Az));
        var sign = Sign(in l);
        for (var j = 0; j < d.Length; j += Vector<double>.Count)
        {
            var v = ax * (new Vector<double>(x[j..]) - ox) + ay * (new Vector<double>(y[j..]) - oy) + az * (new Vector<double>(z[j..]) - oz);
            (v * sign).CopyTo(d[j..]);
        }
    }

    static void RadialLanes(in LeafData l, TapeOp op, ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> d)
    {
        var (ox, oy, oz) = (new Vector<double>(l.Ox), new Vector<double>(l.Oy), new Vector<double>(l.Oz));
        var (ax, ay, az) = (new Vector<double>(l.Ax), new Vector<double>(l.Ay), new Vector<double>(l.Az));
        var (r, minor, cos, sin) = (new Vector<double>(l.R), new Vector<double>(l.Minor), new Vector<double>(l.Cos), new Vector<double>(l.Sin));
        var sign = Sign(in l);
        for (var j = 0; j < d.Length; j += Vector<double>.Count)
        {
            var (vx, vy, vz) = (new Vector<double>(x[j..]) - ox, new Vector<double>(y[j..]) - oy, new Vector<double>(z[j..]) - oz);
            Vector<double> v;
            if (op == TapeOp.Sphere) v = Vector.SquareRoot(vx * vx + vy * vy + vz * vz) - r;
            else
            {
                var axial = vx * ax + vy * ay + vz * az;
                var (rx, ry, rz) = (vx - ax * axial, vy - ay * axial, vz - az * axial);
                var len = Vector.SquareRoot(rx * rx + ry * ry + rz * rz);
                if (op == TapeOp.Cylinder) v = len - r;
                else if (op == TapeOp.Cone) v = len * cos - axial * sin;
                else
                {
                    var rho = len - r;
                    v = Vector.SquareRoot(rho * rho + axial * axial) - minor;
                }
            }
            (v * sign).CopyTo(d[j..]);
        }
    }
}
