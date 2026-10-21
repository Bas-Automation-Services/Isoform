using System.Globalization;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;
using Isoform.Modeling;

namespace Isoform.Interactive;

public enum HandleKind { Linear, Planar, Angle, Frame }

/// <summary>One field a handle drives: dragging by <c>t</c> sets <paramref name="Path"/> to <paramref name="Base"/> + <paramref name="Per"/>·t.</summary>
/// <param name="Per">One entry per dimension of t: one for linear and angle handles, two (along u, v) for planar ones.</param>
public sealed record HandleEdit(string Path, double Base, double[] Per);

/// <summary>A pattern's copy count, stepped with +/−; <paramref name="Path"/> is null when an expression sets it.</summary>
public sealed record HandleCount(string? Path, int Value, int Min);

/// <summary>
/// A drag handle on a feature: where it sits (world coordinates, on the feature's exact surfaces), how it moves and
/// which document fields it edits. <paramref name="Value"/> is the first field's value (two for a planar handle);
/// <see cref="Min"/>, <see cref="Max"/> and <see cref="Step"/> bound and snap it. A handle on a field set by an
/// expression is read-only: it has no edits and names the <see cref="Expression"/>.
/// </summary>
/// <param name="Axis">The drag direction; a planar handle's plane normal; an angle handle's rotation axis (right-handed, t in degrees).</param>
/// <param name="Faces">The faces the handle moves, by source name.</param>
public sealed record Handle(string Id, string Feature, HandleKind Kind, string Label, Point3D Anchor, Vector3D Axis, double[] Value,
    IReadOnlyList<string> Faces, IReadOnlyList<HandleEdit> Edits)
{
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double Step { get; init; } = 1;

    /// <summary>A planar handle's in-plane directions: t = (t·u, t·v).</summary>
    public Vector3D U { get; init; }
    public Vector3D V { get; init; }

    /// <summary>An angle handle's centre: on the axis, level with the anchor.</summary>
    public Point3D Center { get; init; }

    /// <summary>A frame handle's placement as solved (the instance's matrix) and as stored (what its edits set).</summary>
    public Frame Frame { get; init; }
    public Placement? Placement { get; init; }

    public string? Expression { get; init; }
    public HandleCount? Count { get; init; }

    public bool ReadOnly => Expression is not null;
}

/// <summary>
/// The handles of a regeneration, one contract for every feature (see the README): block faces, hole centre and
/// diameter, extrude caps and profile corners, revolve angle, fillet radius, chamfer distance, shell thickness, offset
/// distance, pattern spacing, lattice cell, thickness and skin, and in an assembly a frame per component. Handles sit on the feature's own instance:
/// pattern copies get none of their own.
/// </summary>
public static partial class Handles
{
    // Below this a size is zero: the document rules reject it.
    const double MinSize = 0.001;

    public static IReadOnlyList<Handle> Of(Regeneration? r)
    {
        if (r?.View is not { } view || r.Text is null) return [];
        if (view.Assembly is { } assembly) return Frames(CanonicalFormat.ReadBundle(r.Text).Assembly, assembly);
        if (view.Part is not { Mesh: not null } part) return [];
        var stored = CanonicalFormat.Read(r.Text);
        return new Builder(stored, Parametric.Resolve(stored), part).All();
    }

    static List<Handle> Frames(AssemblyDocument assembly, AssemblyResult result) =>
    [
        .. from c in assembly.Components
           join i in result.Instances on c.Id equals i.Id
           select new Handle($"{c.Id}:placement", c.Id, HandleKind.Frame, $"{c.Id} placement", i.Placement.Origin, i.Placement.Z, [], [], [])
           {
               Frame = i.Placement, Placement = c.Placement,
           },
    ];

    sealed partial class Builder(PartDocument stored, PartDocument doc, FrepResult result)
    {
        readonly IReadOnlyList<Surface> _surfaces = result.Part.Surfaces;
        readonly Mesh _mesh = result.Mesh!;
        readonly List<int>[] _quads = QuadsByOwner(result.Part.Surfaces.Count, result.Mesh!);
        readonly List<Handle> _handles = [];

        static List<int>[] QuadsByOwner(int count, Mesh mesh)
        {
            var quads = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
            for (var q = 0; q < mesh.Quads.Count; q++) quads[mesh.Quads[q].Owner].Add(q);
            return quads;
        }

        public List<Handle> All()
        {
            foreach (var feature in doc.Features)
            {
                switch (feature)
                {
                    case Block b: Block(b); break;
                    case Hole { On: null, Sketch: null } h: Hole(h); break;
                    // A sketch's extrude or revolve gets its handles from the sketch (later); its own are for inline profiles.
                    case Extrude { Sketch: null } e: Extrude(e); break;
                    case Revolve { Sketch: null } r: Revolve(r); break;
                    case Fillet f: Edge(f, f.Edge, f.Loop, f.Radius, "radius", round: true); break;
                    case Chamfer c: Edge(c, c.Edge, c.Loop, c.Distance, "distance", round: false); break;
                    case Shell s: Skin(s, "thickness", s.Thickness); break;
                    case Lattice l: Lattice(l); break;
                    case Offset o: Offset(o); break;
                    case Pattern p: Pattern(p); break;
                }
            }
            return _handles;
        }

        void Block(Block b)
        {
            string[] sizes = ["width", "length", "height"];
            for (var i = 0; i < 3; i++)
            foreach (var high in (bool[])[false, true])
            {
                var name = $"{"xyz"[i]}{(high ? '+' : '-')}";
                var face = $"{b.Id}/{name}";
                var centre = Enumerable.Range(0, 3).Select(j => b.Origin[j] + (j != i ? b.Size[j] / 2 : high ? b.Size[j] : 0)).ToArray();
                var axis = Unit(i) * (high ? 1 : -1);
                // Moving the low face moves the origin and grows the size by as much.
                (string, double, double[])[] fields = high
                    ? [($"size.{i}", b.Size[i], [1.0])]
                    : [($"size.{i}", b.Size[i], [1.0]), ($"origin.{i}", b.Origin[i], [-1.0])];
                // A face the part does not show (a cut's, outside it) still has its plane, where a later offset put it.
                var anchor = Middle(face)?.At ?? OnSurface(Exact(face)!, P(centre));
                _handles.Add(Make(b, name, HandleKind.Linear, $"{b.Id} {sizes[i]}", anchor, axis, [face], fields) with { Min = MinSize });
            }
        }

        void Hole(Hole h)
        {
            var wall = $"{h.Id}/wall";
            // At the top of the wall, where the hole opens; through-all, so the part's top if it cuts nothing.
            var top = Exact(wall) is { } s && _quads[s.Id].Count > 0
                ? _quads[s.Id].Max(q => Corners(_mesh.Quads[q]).Max(v => _mesh.Vertices[v].Z))
                : result.Part.Max.Z;
            var centre = new Point3D(h.Center[0], h.Center[1], top);
            var (x, y) = (Unit(0), Unit(1));
            _handles.Add(Make(h, "center", HandleKind.Planar, $"{h.Id} position", centre, Unit(2), [wall],
                ("center.0", h.Center[0], [1.0, 0]), ("center.1", h.Center[1], [0, 1.0])) with { U = x, V = y });
            _handles.Add(Make(h, "diameter", HandleKind.Linear, $"{h.Id} diameter", centre + x * (h.Diameter / 2), x, [wall],
                ("diameter", h.Diameter, [2.0])) with { Min = MinSize, Step = 0.5 });
        }

        void Extrude(Extrude e)
        {
            var frame = Profiles.FrameOf(e.Plane!);
            var (u0, v0, u1, v1) = Profiles.Bounds(e.Profile!);
            // A cap the part does not show still has its plane, as a block face does.
            Point3D Cap(string face, double w) => Middle(face)?.At ?? OnSurface(Exact(face)!, frame.ToWorld(new Point3D((u0 + u1) / 2, (v0 + v1) / 2, w)));
            var (start, end) = ($"{e.Id}/start", $"{e.Id}/end");
            _handles.Add(Make(e, "end", HandleKind.Linear, $"{e.Id} end", Cap(end, e.End), frame.Z, [end], ("end", e.End, [1.0])) with { Min = e.Start + MinSize });
            _handles.Add(Make(e, "start", HandleKind.Linear, $"{e.Id} start", Cap(start, e.Start), -frame.Z, [start], ("start", e.Start, [-1.0])) with { Max = e.End - MinSize });
            Vertices(e, e.Profile!, frame, e.End);
        }

        void Revolve(Revolve r)
        {
            var frame = Profiles.FrameOf(r.Plane!);
            // A point of the largest side face the part shows (else the corner farthest from the axis) at its height and
            // distance from the axis, turned to the end of the sweep: θ runs from x towards −z.
            var side = r.Profile!.Edges.Select(x => Middle($"{r.Id}/{x.Id}")).Where(m => m is not null).MaxBy(m => m!.Value.Area)?.At;
            var far = r.Profile.Edges.Select(x => x.To).MaxBy(p => p[0])!;
            var (height, radius) = side is { } m && frame.ToLocal(m) is var l ? (l.Y, Math.Sqrt(l.X * l.X + l.Z * l.Z)) : (far[1], far[0]);
            var phi = r.Angle * Math.PI / 180;
            var centre = frame.Origin + frame.Y * height;
            var anchor = centre + (frame.X * Math.Cos(phi) - frame.Z * Math.Sin(phi)) * radius;
            _handles.Add(Make(r, "angle", HandleKind.Angle, $"{r.Id} angle", anchor, frame.Y, r.Angle < 360 ? [$"{r.Id}/end"] : [],
                ("angle", r.Angle, [1.0])) with { Center = centre, Min = MinSize, Max = 360 });
            Vertices(r, r.Profile!, frame, 0);
        }

        /// <summary>
        /// A fillet or chamfer: an arrow at the middle of its face (a loop's largest), on the bisector of the two faces
        /// it joins. A fillet's arc is r / sin(φ/2) − r from the corner for faces φ apart, a chamfer's middle d·cos(φ/2).
        /// </summary>
        void Edge(Feature f, string[] edge, string? loop, double size, string field, bool round)
        {
            // A loop names each face after the face it meets: round/plate/x+ joins the loop face and plate/x+.
            var choices = loop is null
                ? [($"{f.Id}/face", edge[0], edge[1])]
                : _surfaces.Select(s => s.Source).Where(s => s.StartsWith(f.Id + "/")).Distinct().Select(s => (s, loop, s[(f.Id.Length + 1)..])).ToList();
            var chosen = choices
                .Select(c => (Face: c.Item1, Middle: Middle(c.Item1), A: Exact(c.Item2), B: Exact(c.Item3)))
                .Where(c => c.Middle is not null && c.A is { } a && Analytic(a) && c.B is { } b && Analytic(b))
                .OrderByDescending(c => c.Middle!.Value.Area).FirstOrDefault();
            if (chosen.Middle is not { } middle) return;
            var (sa, sb, m) = (chosen.A!, chosen.B!, middle.At);

            var q = Corner(sa, sb, m);
            var along = sa.Gradient(q).Cross(sb.Gradient(q));
            if (along.Length < 1e-6) return; // tangent faces: no corner to measure from
            along = along.Normalized();
            // Along each face away from the edge, on the side the fillet face is.
            Vector3D Away(Vector3D gradient) => along.Cross(gradient).Normalized() is var t && t.Dot(m - q) >= 0 ? t : -t;
            var (ta, tb) = (Away(sa.Gradient(q)), Away(sb.Gradient(q)));
            var sin = Math.Sin(Math.Acos(Math.Clamp(ta.Dot(tb), -1, 1)) / 2);
            if (1 - sin < 1e-6) return;
            var per = round ? sin / (1 - sin) : 1 / Math.Sqrt(1 - sin * sin);
            _handles.Add(Make(f, field, HandleKind.Linear, $"{f.Id} {field}", q + (ta + tb).Normalized() * (size / per), (ta + tb).Normalized(), [chosen.Face],
                (field, size, [per])) with { Min = MinSize, Step = 0.5 });
        }

        /// <summary>
        /// A shell's thickness or a lattice's skin: the arrow sits on the largest outer face and points in, and thickening
        /// moves that face's inner face along it.
        /// </summary>
        void Skin(Feature sh, string field, double thickness)
        {
            var prefix = $"{sh.Id}/inner:";
            var chosen = _surfaces.Select(s => s.Source).Where(s => s.StartsWith(prefix)).Distinct()
                .Select(inner => (Inner: inner, Middle: Middle(inner[prefix.Length..]), Surface: Exact(inner)))
                .Where(c => c.Middle is not null && Analytic(c.Surface!))
                .OrderByDescending(c => c.Middle!.Value.Area).FirstOrDefault();
            if (chosen.Middle is not { } middle) return;
            var (at, s) = (middle.At, chosen.Surface!);
            var axis = (s.Gradient(at) * -s.Distance(at)).Normalized();
            _handles.Add(Make(sh, field, HandleKind.Linear, $"{sh.Id} {field}", at, axis, [chosen.Inner], (field, thickness, [1.0])) with { Min = MinSize, Step = 0.5 });
        }

        /// <summary>
        /// The arrow sits on the largest face the offset moved, along its outward normal. A moved face keeps its name:
        /// its surface replaces an earlier one with the name, the distance away.
        /// </summary>
        void Offset(Offset o)
        {
            var chosen = _surfaces.Select(s => s.Source).Distinct()
                .Select(n => (Face: n, Middle: Middle(n), Surface: Exact(n)))
                .Where(c => c.Middle is not null && c.Surface!.Kind == SurfaceKind.Plane)
                .Select(c => (c.Face, c.Middle, Before: _surfaces.FirstOrDefault(s => s.Source == c.Face && s.Id < c.Surface!.Id && s.Kind == SurfaceKind.Plane
                    && s.Axis.Cross(c.Surface.Axis).Length < 1e-9 && Math.Abs(Math.Abs(s.Distance(c.Middle!.Value.At)) - Math.Abs(o.Distance)) < 1e-9)))
                .Where(c => c.Before is not null)
                .OrderByDescending(c => c.Middle!.Value.Area).FirstOrDefault();
            if (chosen.Middle is not { } middle) return;
            var axis = (middle.At - OnSurface(chosen.Before!, middle.At)) * (1 / o.Distance);
            _handles.Add(Make(o, "distance", HandleKind.Linear, $"{o.Id} distance", middle.At, axis.Normalized(), [chosen.Face], ("distance", o.Distance, [1.0])) with { Step = 0.5 });
        }

        /// <summary>
        /// An arrow at the last copy: a linear pattern's spacing (the last copy moves count − 1 times as far), a partial
        /// circle's angle, a mirror's plane (the copy moves twice as far as the plane). A full circle spreads its copies
        /// evenly and has none.
        /// </summary>
        void Pattern(Pattern p)
        {
            var last = p.Motion switch { LinearMotion l => l.Count - 1, CircularMotion c => c.Count - 1, _ => 1 };
            var prefix = $"{p.Id}/{last}:";
            string[] faces = [.. _surfaces.Select(s => s.Source).Where(s => s.StartsWith(prefix)).Distinct()];
            if (faces.Length == 0) return;
            var at = faces.Select(Middle).Where(m => m is not null).MaxBy(m => m!.Value.Area)?.At ?? Exact(faces[0])!.Origin;
            switch (p.Motion)
            {
                case LinearMotion l:
                    _handles.Add(Make(p, "spacing", HandleKind.Linear, $"{p.Id} spacing", at, V(l.Direction).Normalized(), faces,
                        ("linear.spacing", l.Spacing, [1.0 / (l.Count - 1)])) with { Count = CountOf(p, "linear.count", l.Count) });
                    break;
                case CircularMotion { Angle: < 360 } c:
                    var (origin, axis) = (P(c.Origin), V(c.Axis).Normalized());
                    _handles.Add(Make(p, "angle", HandleKind.Angle, $"{p.Id} angle", at, axis, faces, ("circular.angle", c.Angle, [1.0])) with
                    {
                        Center = origin + axis * axis.Dot(at - origin), Min = MinSize, Max = 360, Count = CountOf(p, "circular.count", c.Count),
                    });
                    break;
                case MirrorMotion m:
                    var n = V(m.Normal).Normalized();
                    double[] normal = [n.X, n.Y, n.Z];
                    (string, double, double[])[] fields = [.. Enumerable.Range(0, 3).Where(j => normal[j] != 0).Select(j => ($"mirror.point.{j}", m.Point[j], (double[])[normal[j] / 2]))];
                    _handles.Add(Make(p, "plane", HandleKind.Linear, $"{p.Id} plane", at, n, faces, fields));
                    break;
            }
        }

        HandleCount CountOf(Pattern p, string field, int count) => new(Bind($"{p.Id}.{field}", out _), count, 2);

        /// <summary>
        /// A handle on fields of a feature: each (field path under the feature, value, per). A field that is a bare
        /// reference to a constant parameter drives the parameter; any other expression makes the handle read-only, as
        /// do two fields on one parameter that would move differently.
        /// </summary>
        Handle Make(Feature f, string name, HandleKind kind, string label, Point3D anchor, Vector3D axis, string[] faces,
            params (string Field, double Value, double[] Per)[] fields)
        {
            var edits = new List<HandleEdit>();
            string? expression = null;
            foreach (var (field, value, per) in fields)
            {
                if (Bind($"{f.Id}.{field}", out var text) is not { } path) expression ??= text;
                else if (edits.FirstOrDefault(e => e.Path == path) is not { } same) edits.Add(new HandleEdit(path, value, per));
                else if (!same.Per.SequenceEqual(per)) expression ??= text;
            }
            double[] shown = kind == HandleKind.Planar ? [fields[0].Value, fields[1].Value] : [fields[0].Value];
            return new Handle($"{f.Id}:{name}", f.Id, kind, label, anchor, axis, shown, faces, expression is null ? edits : []) { Expression = expression };
        }

        /// <summary>The path an edit of a field sets: the field, or the constant parameter it names; null for an expression.</summary>
        string? Bind(string path, out string? text)
        {
            if (!stored.Expressions.TryGetValue(path, out text)) return path;
            var name = text.Trim();
            return Expression.IsName(name) && stored.Parameters.TryGetValue(name, out var definition)
                && double.TryParse(definition, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? $"parameters.{name}" : null;
        }

        /// <summary>
        /// The surface a face name stands for: the one most of the mesh's quads with that name use (an offset face
        /// replaces an earlier surface of its name), else the last registered. Null when the part has no such face.
        /// </summary>
        Surface? Exact(string source)
        {
            Surface? best = null;
            foreach (var s in _surfaces)
                if (s.Source == source && (best is null || _quads[s.Id].Count >= _quads[best.Id].Count)) best = s;
            return best;
        }

        /// <summary>
        /// A point of the face near the middle of its mesh, on its exact surface, and the face's area; null when the
        /// result does not show the face. The quad nearest the area-weighted centre, so the point is on the face even
        /// when the face is not convex.
        /// </summary>
        (Point3D At, double Area)? Middle(string source)
        {
            if (Exact(source) is not { } s || _quads[s.Id].Count == 0) return null;
            var (sum, area) = (new Vector3D(0, 0, 0), 0.0);
            foreach (var q in _quads[s.Id])
            {
                var (c, a) = Piece(_mesh.Quads[q]);
                (sum, area) = (sum + c.AsVector() * a, area + a);
            }
            var centre = sum * (1 / area);
            var nearest = _quads[s.Id].MinBy(q => (Piece(_mesh.Quads[q]).Centre.AsVector() - centre).LengthSquared);
            return (OnSurface(s, Piece(_mesh.Quads[nearest]).Centre), area);
        }

        (Point3D Centre, double Area) Piece(Quad q)
        {
            var (a, b, c, d) = (_mesh.Vertices[q.A], _mesh.Vertices[q.B], _mesh.Vertices[q.C], _mesh.Vertices[q.D]);
            var centre = q.IsTriangle
                ? new Point3D((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3)
                : new Point3D((a.X + b.X + c.X + d.X) / 4, (a.Y + b.Y + c.Y + d.Y) / 4, (a.Z + b.Z + c.Z + d.Z) / 4);
            return (centre, (c - a).Cross(d - b).Length / 2);
        }

        static int[] Corners(Quad q) => q.IsTriangle ? [q.A, q.B, q.C] : [q.A, q.B, q.C, q.D];
    }

    static bool Analytic(Surface s) => s.Analytic;

    /// <summary>The point moved onto an analytic surface along its gradient (a fillet or blend face keeps the mesh point).</summary>
    static Point3D OnSurface(Surface s, Point3D p)
    {
        if (!Analytic(s)) return p;
        for (var i = 0; i < 4; i++) p -= s.Gradient(p) * s.Distance(p);
        return p;
    }

    /// <summary>The point of the edge where two surfaces meet nearest to p: Newton steps of least length onto both.</summary>
    static Point3D Corner(Surface a, Surface b, Point3D p)
    {
        for (var i = 0; i < 20; i++)
        {
            var (ga, gb, da, db) = (a.Gradient(p), b.Gradient(p), a.Distance(p), b.Distance(p));
            var (aa, ab, bb) = (ga.Dot(ga), ga.Dot(gb), gb.Dot(gb));
            var det = aa * bb - ab * ab;
            if (det < 1e-12) break;
            p -= ga * ((bb * da - ab * db) / det) + gb * ((aa * db - ab * da) / det);
        }
        return p;
    }

    static Vector3D Unit(int i) => new(i == 0 ? 1 : 0, i == 1 ? 1 : 0, i == 2 ? 1 : 0);
    static Point3D P(double[] v) => new(v[0], v[1], v[2]);
    static Vector3D V(double[] v) => new(v[0], v[1], v[2]);
}
