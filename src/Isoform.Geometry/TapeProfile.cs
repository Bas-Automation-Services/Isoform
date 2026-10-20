using BasAutomation.Geometry;

namespace Isoform.Geometry;

/// <summary>
/// One profile edge unpacked for the tape, with the leaf it sweeps and the corner at its end. The chord terms are
/// computed once here, as <see cref="ProfileEdge2D.Closest"/> computes them on every call: the same numbers.
/// </summary>
readonly struct ProfileEdgeData
{
    public ProfileEdgeData(ProfileEdge2D e, int leaf, TapeOp op, bool smooth, double sign, int corner)
    {
        (U0, V0, U1, V1, IsArc, Cu, Cv, R, A0, Sweep) = (e.U0, e.V0, e.U1, e.V1, e.IsArc, e.Cu, e.Cv, e.R, e.A0, e.Sweep);
        (Du, Dv) = (e.U1 - e.U0, e.V1 - e.V0);
        Len2 = Du * Du + Dv * Dv;
        AbsSweep = Math.Abs(e.Sweep);
        (Leaf, Op, Smooth, Sign, Corner) = (leaf, op, smooth, sign, corner);
    }

    public readonly double U0, V0, U1, V1, Du, Dv, Len2, Cu, Cv, R, A0, Sweep, AbsSweep;
    public readonly bool IsArc;

    /// <summary>The leaf data of the edge's surface and its instruction; −1 for an edge on the revolve axis.</summary>
    public readonly int Leaf;

    public readonly TapeOp Op;

    /// <summary>The corner at the end: the edge's own surface decides there, its sign, and its round face (or −1).</summary>
    public readonly bool Smooth;

    public readonly double Sign;
    public readonly int Corner;

    /// <summary><see cref="ProfileEdge2D.Closest"/>, operation for operation.</summary>
    public (double Squared, int Where) Closest(double u, double v)
    {
        if (IsArc)
        {
            if (WithinSweep(u, v))
            {
                var radial = Math.Sqrt((u - Cu) * (u - Cu) + (v - Cv) * (v - Cv)) - R;
                return (radial * radial, -1);
            }
        }
        else
        {
            var t = ((u - U0) * Du + (v - V0) * Dv) / Len2;
            if (t > 0 && t < 1)
            {
                var (eu, ev) = (u - U0 - t * Du, v - V0 - t * Dv);
                return (eu * eu + ev * ev, -1);
            }
        }
        var s0 = (u - U0) * (u - U0) + (v - V0) * (v - V0);
        var s1 = (u - U1) * (u - U1) + (v - V1) * (v - V1);
        return s0 <= s1 ? (s0, 0) : (s1, 1);
    }

    bool WithinSweep(double u, double v)
    {
        var a = Math.Atan2(v - Cv, u - Cu);
        var rel = Sweep > 0 ? a - A0 : A0 - a;
        rel %= 2 * Math.PI;
        if (rel < 0) rel += 2 * Math.PI;
        return rel < AbsSweep;
    }
}

/// <summary>A <see cref="ProfileField"/> unpacked: its edges, frame and offset, and its leaves in its own order.</summary>
sealed class ProfileData
{
    public ProfileData(ProfileField f, Func<LeafField, int> leaf)
    {
        var (edges, leaves, corners, signs, offset) = f.Parts;
        Field = f;
        Edges = new ProfileEdgeData[edges.Length];
        for (var k = 0; k < edges.Length; k++)
            Edges[k] = new(edges[k], leaves[k] is { } l ? leaf(l) : -1, leaves[k] is { } m ? Tape.LeafOp(m) : default,
                f.Smooth(k), signs[k], corners[k]?.Surface.Id ?? -1);
        Leaves = f.Leaves.Select(l => (leaf(l), Tape.LeafOp(l))).ToArray();
        var (o, x, y) = (f.Frame.Origin, f.Frame.X, f.Frame.Y);
        (Ox, Oy, Oz, Xx, Xy, Xz, Yx, Yy, Yz) = (o.X, o.Y, o.Z, x.X, x.Y, x.Z, y.X, y.Y, y.Z);
        (Revolve, Offset) = (f.Revolved, offset);
    }

    public readonly ProfileField Field;
    public readonly ProfileEdgeData[] Edges;

    /// <summary>The leaves as <see cref="ProfileField.Leaves"/> lists them: edges first, then round corners.</summary>
    public readonly (int Data, TapeOp Op)[] Leaves;

    // The frame's origin and first two axes as numbers: in plain doubles the JIT keeps all of it in registers.
    public readonly double Ox, Oy, Oz, Xx, Xy, Xz, Yx, Yy, Yz;
    public readonly bool Revolve;
    public readonly double Offset;

    /// <summary>
    /// ProfileField.Map as the vector operations compute it, component by component: profile coordinates, and for a
    /// revolve the offset (rx, ry, rz) from the axis.
    /// </summary>
    public void Map(Point3D p, out double u, out double v, out double rx, out double ry, out double rz)
    {
        var (dx, dy, dz) = (p.X - Ox, p.Y - Oy, p.Z - Oz);
        if (!Revolve)
        {
            (u, v) = (dx * Xx + dy * Xy + dz * Xz, dx * Yx + dy * Yy + dz * Yz);
            (rx, ry, rz) = (0, 0, 0);
            return;
        }
        v = dx * Yx + dy * Yy + dz * Yz;
        (rx, ry, rz) = (dx - Yx * v, dy - Yy * v, dz - Yz * v);
        u = Math.Sqrt(rx * rx + ry * ry + rz * rz);
    }
}

public sealed partial class Tape
{
    // ---- Profiles: the operations of ProfileField, in the same order ----

    /// <summary>ProfileField.Value.</summary>
    double ProfileValue(ProfileData pd, Point3D p)
    {
        pd.Map(p, out var u, out var v, out _, out _, out _);
        var (i, where, k) = Nearest(pd.Edges, u, v);
        ref readonly var e = ref pd.Edges[i];
        if (where < 0 || pd.Edges[k].Smooth) return LeafValue(in _leaves[e.Leaf], e.Op, p);
        var (du, dv) = where == 1 ? (u - e.U1, v - e.V1) : (u - e.U0, v - e.V0);
        return pd.Edges[k].Sign * Math.Sqrt(du * du + dv * dv) - pd.Offset;
    }

    /// <summary>ProfileField.Sample: a corner's distance points away from the corner, and is owned by its round face if it has one.</summary>
    FieldSample ProfileSample(ProfileData pd, Point3D p)
    {
        pd.Map(p, out var u, out var v, out var rx, out var ry, out var rz);
        var (i, where, k) = Nearest(pd.Edges, u, v);
        ref readonly var e = ref pd.Edges[i];
        if (where < 0 || pd.Edges[k].Smooth) return LeafSample(in _leaves[e.Leaf], e.Op, p);
        var (du, dv) = where == 1 ? (u - e.U1, v - e.V1) : (u - e.U0, v - e.V0);
        var sign = pd.Edges[k].Sign;
        var dist = Math.Sqrt(du * du + dv * dv);
        if (!(dist > 0)) return new(sign * dist - pd.Offset, Owner(pd, k, e.Leaf), LeafSample(in _leaves[e.Leaf], e.Op, p).Gradient);
        // (radial · gu + Y · gv) · sign, with radial the frame's X for an extrude or on the axis.
        var (gu, gv) = (du / dist, dv / dist);
        var (ax, ay, az) = !pd.Revolve || !(u > 0) ? (pd.Xx, pd.Xy, pd.Xz) : (rx / u, ry / u, rz / u);
        var g = new Vector3D((ax * gu + pd.Yx * gv) * sign, (ay * gu + pd.Yy * gv) * sign, (az * gu + pd.Yz * gv) * sign);
        return new(sign * dist - pd.Offset, Owner(pd, k, e.Leaf), g);
    }

    /// <summary>A corner point belongs to the corner's round face if it has one, else to the edge.</summary>
    int Owner(ProfileData pd, int corner, int leaf) => pd.Edges[corner].Corner is var c and >= 0 ? c : _leaves[leaf].Id;

    /// <summary>The nearest edge with a surface, where on it (−1 inside, 0 start, 1 end), and the corner at that end.</summary>
    static (int Edge, int Where, int Corner) Nearest(ProfileEdgeData[] edges, double u, double v)
    {
        var (best, where, squared) = (-1, 0, double.PositiveInfinity);
        for (var i = 0; i < edges.Length; i++)
        {
            if (edges[i].Leaf < 0) continue;
            var (s, w) = edges[i].Closest(u, v);
            if (s < squared) (best, where, squared) = (i, w, s);
        }
        return (best, where, where == 1 ? best : (best - 1 + edges.Length) % edges.Length);
    }

    /// <summary>ProfileField.SampleSurface: the first of its leaves with that owner.</summary>
    FieldSample? ProfileSurface(ProfileData pd, int owner, Point3D p)
    {
        foreach (var (data, op) in pd.Leaves)
            if (_leaves[data].Id == owner) return LeafSample(in _leaves[data], op, p);
        return null;
    }
}
