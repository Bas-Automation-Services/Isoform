using BasAutomation.Geometry;
using Isoform.Geometry;
using Isoform.Meshing;
using static System.FormattableString;

namespace Isoform.Modeling;

/// <summary>A point of a section loop in the plane's coordinates, on the solid's boundary, and the surface owning it there.</summary>
public readonly record struct SectionPoint(Point2D At, int Owner);

/// <summary>A closed loop of a plane section with the material on its left: counter-clockwise round an outer loop, clockwise round a hole.</summary>
public sealed record SectionLoop(IReadOnlyList<SectionPoint> Points)
{
    /// <summary>The signed area enclosed: positive for an outer loop.</summary>
    public double Area
    {
        get
        {
            var a = 0.0;
            for (var i = 0; i < Points.Count; i++)
            {
                var (p, q) = (Points[i].At, Points[(i + 1) % Points.Count].At);
                a += p.X * q.Y - q.X * p.Y;
            }
            return a / 2;
        }
    }

    public bool Outer => Area > 0;

    public bool Contains(Point2D p)
    {
        var inside = false;
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
        {
            var (a, b) = (Points[i].At, Points[j].At);
            if (a.Y > p.Y != b.Y > p.Y && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}

/// <summary>A region of a section: an outer loop and the holes in it, by index into <see cref="Section.Loops"/>.</summary>
public sealed record SectionRegion(int Outer, IReadOnlyList<int> Holes);

/// <summary>
/// The section of a solid by a plane: its loops in the plane's coordinates (x along <see cref="Frame.X"/>, y along
/// <see cref="Frame.Y"/>), every point on the boundary to rounding and labelled with its surface, the regions they
/// bound, and what the cells could not resolve.
/// </summary>
public sealed class Section(Field field, Frame plane, double cellSize, IReadOnlyList<SectionLoop> loops, IReadOnlyList<SectionRegion> regions, IReadOnlyList<Diagnostic> diagnostics)
{
    /// <summary>The solid, in world coordinates.</summary>
    public Field Field { get; } = field;

    /// <summary>The section plane: its origin and in-plane axes give the 2D coordinates, Z is its normal.</summary>
    public Frame Plane { get; } = plane;

    public double CellSize { get; } = cellSize;
    public IReadOnlyList<SectionLoop> Loops { get; } = loops;
    public IReadOnlyList<SectionRegion> Regions { get; } = regions;
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;

    public Point3D World(Point2D p) => Plane.ToWorld(new Point3D(p.X, p.Y, 0));

    /// <summary>The field at a plane point (or one owner's own field), and its gradient within the plane.</summary>
    public (double Value, int Owner, Vector2D Gradient) Sample(Point2D p, int owner = -1)
    {
        var s = owner < 0 ? Field.Sample(World(p)) : Field.SampleSurface(owner, World(p))!.Value;
        return (s.Value, s.Owner, new Vector2D(s.Gradient.Dot(Plane.X), s.Gradient.Dot(Plane.Y)));
    }

    /// <summary>The unit tangent of the boundary through <paramref name="p"/>, material on its left.</summary>
    public Vector2D Tangent(Point2D p, int owner = -1)
    {
        var g = Sample(p, owner).Gradient.Normalized();
        return new Vector2D(-g.Y, g.X);
    }

    /// <summary>
    /// <paramref name="p"/> moved onto the boundary within the plane by Newton steps along the in-plane gradient: of the
    /// solid, or of one owner's own surface. Null where the boundary runs along the plane or the steps do not settle.
    /// </summary>
    public Point2D? Project(Point2D p, int owner = -1)
    {
        for (var it = 0; it < 60; it++)
        {
            var (value, _, g) = Sample(p, owner);
            var gg = g.LengthSquared;
            if (gg < 1e-12) return null;
            var step = g * (value / gg);
            p -= step;
            if (step.Length <= 1e-15 * (1 + Math.Abs(p.X) + Math.Abs(p.Y))) return p;
        }
        return Math.Abs(Sample(p, owner).Value) <= 1e-12 * (1 + Math.Abs(p.X) + Math.Abs(p.Y)) ? p : null;
    }
}

/// <summary>
/// Plane sections of a solid. A slab one cell either side of the plane is meshed by the dual contouring of the solid
/// (with the guarantees and reports of a 3D regeneration), the mesh is sliced at the plane, and every crossing is moved
/// onto the boundary within the plane by Newton steps on the field, keeping the surface that owns it there.
/// </summary>
public static class Sections
{
    /// <param name="surfaces">The solid's surfaces by owner id, for naming what a report is about.</param>
    /// <param name="bounds">A world box holding the solid (or the part of it to section).</param>
    public static Section Of(Field field, IReadOnlyList<Surface> surfaces, Frame plane, Box3D bounds, double cellSize, double chordTolerance = 0.01, double tolerance = 1e-7)
    {
        // Mesh in the plane's own frame, where the slab is axis-aligned: the field placed by the frame's inverse.
        var inverse = new Frame(new Point3D(0, 0, 0) - plane.ToLocal(new Vector3D(plane.Origin.X, plane.Origin.Y, plane.Origin.Z)),
            new Vector3D(plane.X.X, plane.Y.X, plane.Z.X), new Vector3D(plane.X.Y, plane.Y.Y, plane.Z.Y), new Vector3D(plane.X.Z, plane.Y.Z, plane.Z.Z));
        var h = cellSize;
        var (top, bottom) = (surfaces.Count, surfaces.Count + 1);
        var slab = new MaxField([new PlacedField(field, inverse),
            new LeafField(new Surface(top, "", SurfaceKind.Plane, new Point3D(0, 0, h), new Vector3D(0, 0, 1))),
            new LeafField(new Surface(bottom, "", SurfaceKind.Plane, new Point3D(0, 0, -h), new Vector3D(0, 0, -1)))]);
        var (lo, hi) = (new Point3D(double.MaxValue, double.MaxValue, -h), new Point3D(double.MinValue, double.MinValue, h));
        foreach (var x in new[] { bounds.Min.X, bounds.Max.X })
        foreach (var y in new[] { bounds.Min.Y, bounds.Max.Y })
        foreach (var z in new[] { bounds.Min.Z, bounds.Max.Z })
        {
            var q = plane.ToLocal(new Point3D(x, y, z));
            (lo, hi) = (new Point3D(Math.Min(lo.X, q.X), Math.Min(lo.Y, q.Y), -h), new Point3D(Math.Max(hi.X, q.X), Math.Max(hi.Y, q.Y), h));
        }
        var mesh = DualContouring.Build(slab, lo, hi, cellSize, chordTolerance);

        var section = new Section(field, plane, cellSize, [], [], []);
        var diagnostics = new List<Diagnostic>();
        var loops = new List<SectionLoop>();
        var chains = Chains(mesh, top, out var open);
        foreach (var keys in chains)
        {
            var points = new List<SectionPoint>();
            foreach (var key in keys)
            {
                var (a, b) = (mesh.Vertices[(int)(key >> 32)], mesh.Vertices[(int)(key & 0xFFFFFFFF)]);
                var t = a.Z / (a.Z - b.Z);
                if (section.Project(new Point2D(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t)) is not { } p) continue;
                if (points.Count > 0 && (p - points[^1].At).Length <= 1e-9) continue;
                points.Add(new SectionPoint(p, section.Sample(p).Owner));
            }
            if (points.Count > 1 && (points[0].At - points[^1].At).Length <= 1e-9) points.RemoveAt(points.Count - 1);
            if (points.Count < 3) continue;
            loops.Add(Oriented(section, points));
        }
        foreach (var at in open)
        {
            var w = plane.ToWorld(at);
            diagnostics.Add(new Diagnostic(Diagnostic.BelowResolution, null,
                Invariant($"the section's loop breaks off near ({w.X:F2}, {w.Y:F2}, {w.Z:F2}): something there is below the resolved size of {2 * cellSize:G4} mm")));
        }

        // What the cells could not resolve within the slab, as a regeneration reports it; the later feature's.
        string Feature(int owner) => surfaces[owner].Source.Split('/')[0];
        foreach (var g in mesh.ThinSpots.Where(s => s.OwnerB < top).GroupBy(s => (s.OwnerA, s.OwnerB)))
        {
            var s = g.MinBy(x => x.Thickness);
            var w = plane.ToWorld(s.Location);
            diagnostics.Add(new Diagnostic(Diagnostic.BelowResolution, Feature(s.OwnerB),
                Invariant($"{surfaces[s.OwnerA].Source} and {surfaces[s.OwnerB].Source} are {s.Thickness:G3} mm apart in the section near ({w.X:F2}, {w.Y:F2}, {w.Z:F2}), below the resolved size of {2 * cellSize:G4} mm")));
        }
        // A pocket counts where it reaches the plane: there the loops say material and the field a gap, or the other way.
        // One report per feature, the largest, unless a thin spot already named it.
        var lost = new List<(Pocket Pocket, int Owner)>();
        foreach (var p in mesh.Pockets.Where(p => Math.Abs(p.Value) > tolerance))
        {
            var at = new Point2D(p.Location.X, p.Location.Y);
            var (value, owner, _) = section.Sample(at);
            if (Math.Abs(value) > tolerance && (value < 0) != (loops.Count(l => l.Contains(at)) % 2 == 1)) lost.Add((p, owner));
        }
        var reported = diagnostics.Select(d => d.FeatureId).ToHashSet();
        foreach (var g in lost.GroupBy(x => Feature(x.Owner)).Where(g => !reported.Contains(g.Key)))
        {
            var (p, owner) = g.MaxBy(x => Math.Abs(x.Pocket.Value));
            var w = plane.ToWorld(p.Location);
            diagnostics.Add(new Diagnostic(Diagnostic.BelowResolution, g.Key,
                Invariant($"{surfaces[owner].Source} leaves {(p.Value < 0 ? "material" : "a gap")} {2 * Math.Abs(p.Value):G3} mm across in the section near ({w.X:F2}, {w.Y:F2}, {w.Z:F2}) that the cells lose whole, below the resolved size of {2 * cellSize:G4} mm")));
        }

        return new Section(field, plane, cellSize, loops, Regions(loops), diagnostics);
    }

    /// <summary>
    /// The closed chains of the mesh's crossings of z = 0, each a list of mesh edges (two vertex indices in a key).
    /// Every polygon of the solid's own surfaces that the plane cuts adds a step between two of its edges; the slab's
    /// caps never reach the plane. Where a chain does not close, the mesh is not manifold there: its point is returned.
    /// </summary>
    static List<List<long>> Chains(Mesh mesh, int caps, out List<Point3D> open)
    {
        var v = mesh.Vertices;
        static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        var steps = new List<(long A, long B)>();
        foreach (var t in mesh.Triangles())
        {
            if (t.Owner >= caps) continue;
            Span<int> c = [t.A, t.B, t.C];
            Span<long> cut = stackalloc long[2];
            var n = 0;
            for (var k = 0; k < 3; k++)
            {
                var (a, b) = (c[k], c[(k + 1) % 3]);
                if (v[a].Z >= 0 != v[b].Z >= 0 && n < 2) cut[n++] = Key(a, b);
            }
            if (n == 2) steps.Add((cut[0], cut[1]));
        }
        var at = new Dictionary<long, List<int>>();
        for (var s = 0; s < steps.Count; s++)
        {
            foreach (var key in new[] { steps[s].A, steps[s].B })
            {
                if (!at.TryGetValue(key, out var list)) at[key] = list = [];
                list.Add(s);
            }
        }
        var chains = new List<List<long>>();
        open = [];
        var used = new bool[steps.Count];
        for (var first = 0; first < steps.Count; first++)
        {
            if (used[first]) continue;
            used[first] = true;
            var chain = new List<long> { steps[first].A };
            var (key, step) = (steps[first].B, first);
            var closed = true;
            while (key != steps[first].A)
            {
                chain.Add(key);
                var next = at[key].Count == 2 ? at[key].First(s => s != step) : -1;
                if (next < 0 || used[next])
                {
                    closed = false;
                    var (a, b) = (v[(int)(key >> 32)], v[(int)(key & 0xFFFFFFFF)]);
                    open.Add(new Point3D((a.X + b.X) / 2, (a.Y + b.Y) / 2, 0));
                    break;
                }
                used[next] = true;
                key = steps[next].A == key ? steps[next].B : steps[next].A;
                step = next;
            }
            if (closed) chains.Add(chain);
        }
        return chains;
    }

    /// <summary>The loop turned, if need be, so the material is on its left: the gradient points out, to the right.</summary>
    static SectionLoop Oriented(Section section, List<SectionPoint> points)
    {
        var vote = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var t = points[(i + 1) % points.Count].At - points[(i + points.Count - 1) % points.Count].At;
            vote += Math.Sign(t.Cross(section.Sample(points[i].At).Gradient));
        }
        if (vote > 0) points.Reverse();
        return new SectionLoop(points);
    }

    /// <summary>Every outer loop with the holes directly inside it: a hole belongs to the smallest outer loop round it.</summary>
    static List<SectionRegion> Regions(List<SectionLoop> loops)
    {
        var holes = new Dictionary<int, List<int>>();
        for (var i = 0; i < loops.Count; i++) if (loops[i].Outer) holes[i] = [];
        for (var i = 0; i < loops.Count; i++)
        {
            if (loops[i].Outer) continue;
            var p = loops[i].Points[0].At;
            var round = holes.Keys.Where(o => loops[o].Contains(p)).OrderBy(o => loops[o].Area).FirstOrDefault(-1);
            if (round >= 0) holes[round].Add(i);
        }
        return holes.Select(kv => new SectionRegion(kv.Key, kv.Value)).ToList();
    }
}
