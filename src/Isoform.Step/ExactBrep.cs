using BasAutomation.Geometry;
using Isoform.Geometry;
using Isoform.Modeling;

namespace Isoform.Step;

/// <summary>
/// Turns an Isoform result into an exact B-rep. The mesh only supplies the combinatorics and the seeds: which faces
/// meet along which chains, in which order round each face. All geometry is exact: each face is its analytic
/// surface, each edge the closed-form intersection of its two surfaces, each vertex the point where an edge curve
/// crosses a third surface. Loops come from the mesh boundary of each face, whose quads wind counter-clockwise seen
/// from outside, so every loop already runs with its face on the left. Faces on periodic surfaces get the seams a
/// reader needs, and every coedge on a curved face its image in the face's parameters.
/// </summary>
public static class ExactBrep
{
    /// <summary>
    /// Builds the B-rep of every body: one solid per body, a shell's cavity as a void. Throws
    /// <see cref="NotSupportedException"/> naming the faces or edges without a closed form.
    /// </summary>
    public static Brep Build(FrepResult result, ModelingSettings settings) => new Builder(result, settings).Run();

    /// <summary>One face's boundary as the mesh walks it: the face across each mesh edge, and the mesh vertices.</summary>
    sealed record Run(int Other, List<int> Vertices);

    /// <summary>A face of a loop fillet or chamfer: its loop, and the side face it runs along with that face's material side.</summary>
    sealed record LoopFace(LoopEdgeField Loop, LeafField Side, bool SideComplement, ProfileField? Profile);

    sealed class Builder(FrepResult result, ModelingSettings settings)
    {
        readonly IReadOnlyList<Point3D> _points = result.Mesh!.Vertices;
        readonly List<BrepVertex> _vertices = [];
        // Edges as they are found; a closed edge's vertex (−1) is placed once the seams know where they run.
        readonly List<(Curve Curve, int Start, int End)> _edges = [];
        readonly Dictionary<long, int> _edgeOfKey = [];
        readonly Dictionary<int, int> _vertexOfMesh = [];
        readonly Dictionary<int, HashSet<int>> _facesAt = [];
        readonly Dictionary<int, RoundedEdgeField> _rounded = [];
        readonly Dictionary<int, LoopFace> _loops = [];
        readonly HashSet<double> _shells = [];
        Surface[] _surface = [];
        Surface[] _owner = [];
        HashSet<int>[] _meshVertices = [];
        HashSet<int>[] _interior = [];

        // A point inside each face (a quad centre), to tell which way a seam runs.
        Point3D[] _inside = [];

        public Brep Run()
        {
            var mesh = result.Mesh!;
            var topology = result.Topology!;
            var faceOfQuad = result.FaceOfQuad;
            var faceCount = topology.Faces.Count;
            if (result.Part.Field is { } field) Collect(field);

            // Each face's exact surface, checked against the face's mesh.
            _owner = new Surface[faceCount];
            _meshVertices = Enumerable.Range(0, faceCount).Select(_ => new HashSet<int>()).ToArray();
            for (var q = 0; q < mesh.Quads.Count; q++)
            {
                var quad = mesh.Quads[q];
                _owner[faceOfQuad[q]] ??= result.Part.Surfaces[quad.Owner];
                _meshVertices[faceOfQuad[q]].UnionWith([quad.A, quad.B, quad.C, quad.D]);
            }
            // Only vertices inside a face lie on its surface alone; the mesh puts edge vertices on two at once.
            var faceSets = new Dictionary<int, HashSet<int>>();
            for (var q = 0; q < mesh.Quads.Count; q++)
                foreach (var v in new[] { mesh.Quads[q].A, mesh.Quads[q].B, mesh.Quads[q].C, mesh.Quads[q].D })
                {
                    if (!faceSets.TryGetValue(v, out var set)) faceSets[v] = set = [];
                    set.Add(faceOfQuad[q]);
                }
            _interior = _meshVertices.Select(all => all.Where(v => faceSets[v].Count == 1).ToHashSet()).ToArray();
            _surface = _owner.Select((o, f) => Exact(o, f)).ToArray();
            var unsupported = Enumerable.Range(0, faceCount).Where(f => _surface[f].Kind is SurfaceKind.Fillet or SurfaceKind.Blend)
                .Select(f => $"{Name(f)} ({_surface[f].Kind})").ToList();
            if (unsupported.Count > 0) throw new NotSupportedException($"faces without a closed-form surface: {string.Join(", ", unsupported)}");
            var stray = Enumerable.Range(0, faceCount).Select(f => (f, Deviation(_surface[f], f))).Where(x => x.Item2 > Fit).ToList();
            if (stray.Count > 0)
                throw new NotSupportedException($"faces that are not one analytic surface: {string.Join(", ", stray.Select(x => $"{Name(x.f)} (off its {_surface[x.f].Kind.ToString().ToLowerInvariant()} by {x.Item2:G2} mm)"))}");

            // Whether each face's gradient points out of the solid (the quads wind outward).
            _inside = new Point3D[faceCount];
            var sense = new double[faceCount];
            for (var q = 0; q < mesh.Quads.Count; q++)
            {
                var quad = mesh.Quads[q];
                var (a, b, c, d) = (_points[quad.A], _points[quad.B], _points[quad.C], _points[quad.D]);
                var centre = new Point3D((a.X + b.X + c.X + d.X) / 4, (a.Y + b.Y + c.Y + d.Y) / 4, (a.Z + b.Z + c.Z + d.Z) / 4);
                sense[faceOfQuad[q]] += (c - a).Cross(d - b).Dot(_surface[faceOfQuad[q]].Gradient(centre));
                _inside[faceOfQuad[q]] = centre;
            }

            // Boundary half-edges per face: quad edges whose neighbour quad lies in another face.
            var quadsOfEdge = new Dictionary<(int, int), List<int>>();
            for (var q = 0; q < mesh.Quads.Count; q++)
                foreach (var (a, b) in Sides(mesh.Quads[q]))
                {
                    var key = (Math.Min(a, b), Math.Max(a, b));
                    if (!quadsOfEdge.TryGetValue(key, out var list)) quadsOfEdge[key] = list = new List<int>(2);
                    list.Add(q);
                }
            var halves = Enumerable.Range(0, faceCount).Select(_ => new List<(int From, int To, int Other)>()).ToArray();
            for (var q = 0; q < mesh.Quads.Count; q++)
                foreach (var (a, b) in Sides(mesh.Quads[q]))
                {
                    var around = quadsOfEdge[(Math.Min(a, b), Math.Max(a, b))];
                    if (around.Count != 2) throw new NotSupportedException("the mesh is not a 2-manifold");
                    var other = faceOfQuad[around[0] == q ? around[1] : around[0]];
                    if (other != faceOfQuad[q]) halves[faceOfQuad[q]].Add((a, b, other));
                }
            for (var q = 0; q < mesh.Quads.Count; q++)
                foreach (var (a, _) in Sides(mesh.Quads[q]))
                {
                    if (!_facesAt.TryGetValue(a, out var set)) _facesAt[a] = set = [];
                    set.Add(faceOfQuad[q]);
                }

            var outward = sense.Select(s => s > 0).ToArray();
            var loops = new List<BrepLoop>[faceCount];
            var loopPoints = new List<List<Point3D>>[faceCount];
            for (var f = 0; f < faceCount; f++)
            {
                var found = Cycles(halves[f]).Select(cycle => Loop(f, cycle)).ToList();
                var planar = _surface[f].Kind == SurfaceKind.Plane;
                loops[f] = found.Select(l => l.Loop with
                {
                    Outer = found.Count == 1 || planar && SignedArea(l.Points, _surface[f].Axis * (outward[f] ? 1 : -1)) > 0,
                }).ToList();
                loopPoints[f] = found.Select(l => l.Points).ToList();
            }
            Seams(loops, loopPoints, outward);
            for (var e = 0; e < _edges.Count; e++)
                if (_edges[e].Start < 0) _edges[e] = _edges[e] with { Start = AddVertex(_edges[e].Curve.At(0)), End = AddVertex(_edges[e].Curve.At(0)) };

            var (shellOf, isVoid) = Shells(loops, mesh, faceOfQuad);
            for (var f = 0; f < faceCount; f++) loops[f] = loops[f].Select(l => SeamsForwardLast(l, outward[f], isVoid[f])).ToList();
            var edges = Enumerable.Range(0, _edges.Count).Select(Finish).ToList();
            var faces = Enumerable.Range(0, faceCount)
                .Select(f => new BrepFace(f, topology.Faces[f].Sources, _surface[f], outward[f], WithImages(f, loops[f], edges))).ToList();
            return new Brep(_vertices, edges, Solids(faces, shellOf, isVoid));
        }

        /// <summary>
        /// Turns every seam of a loop so that its last use, as the face is written, runs forward as the face is
        /// oriented there (along the edge on a face with the surface's sense, against it otherwise). OCCT's reader
        /// gives a seam's two p-curves to its uses one at a time and the last one decides, which comes out right
        /// only that way round (as OCCT writes seams itself). A void's faces are written turned round, so there
        /// it is the first use that is written last.
        /// </summary>
        BrepLoop SeamsForwardLast(BrepLoop loop, bool sameSense, bool turned)
        {
            var coedges = loop.Coedges.ToList();
            foreach (var seam in coedges.GroupBy(c => c.Edge).Where(g => g.Count() == 2).Select(g => g.Key).ToList())
            {
                var use = turned ? coedges.First(c => c.Edge == seam) : coedges.Last(c => c.Edge == seam);
                if (use.Forward == sameSense) continue;
                var (curve, start, end) = _edges[seam];
                _edges[seam] = (curve switch
                {
                    Line l => l with { Direction = -l.Direction },
                    Circle c => c with { Normal = -c.Normal },
                    _ => curve,
                }, end, start);
                coedges = coedges.Select(c => c.Edge == seam ? c with { Forward = !c.Forward } : c).ToList();
            }
            return loop with { Coedges = coedges };
        }

        // How far a face's mesh may lie off its exact surface: the mesh is within the chord tolerance of the true
        // surface away from edges, and edge vertices sit on two surfaces at once.
        double Fit => Math.Max(4 * settings.ChordTolerance, 0.05 * settings.CellSize);

        double Deviation(Surface s, int face) =>
            s.Kind is SurfaceKind.Fillet or SurfaceKind.Blend ? 0 : _interior[face].Select(v => Math.Abs(s.Distance(_points[v]))).DefaultIfEmpty(0).Max();

        /// <summary>The fillets, loops and shells of the tree, by the surfaces they own.</summary>
        void Collect(Field f)
        {
            switch (f)
            {
                case RoundedEdgeField r:
                    _rounded.TryAdd(r.Fillet.Id, r);
                    break;
                case LoopEdgeField l:
                    foreach (var (sideOwner, face) in l.FaceOf)
                        if (LeafIn(l.Side, sideOwner, false) is { } side)
                            _loops.TryAdd(face.Id, new LoopFace(l, side.Leaf, side.Complement,
                                Nodes(l.Side).OfType<ProfileField>().FirstOrDefault(p => p.Leaves.Contains(side.Leaf))));
                    break;
                case ShellField s:
                    _shells.Add(s.Thickness);
                    break;
            }
            foreach (var c in Children(f)) Collect(c);
        }

        static IEnumerable<Field> Children(Field f) => f switch
        {
            MaxField m => m.Children,
            MinField m => m.Children,
            NegateField n => [n.Child],
            RoundedEdgeField r => [r.A, r.B],
            LoopEdgeField l => [l.Cap, l.Side],
            BlendField b => [b.A, b.B],
            ProfileField p => p.Leaves,
            ShellField s => ReferenceEquals(s.Outer, s.Child) ? [s.Child] : [s.Child, s.Outer],
            _ => [],
        };

        static IEnumerable<Field> Nodes(Field f) => Children(f).SelectMany(Nodes).Prepend(f);

        /// <summary>The leaf owning <paramref name="id"/> under <paramref name="f"/>, and whether its material lies outside it there.</summary>
        static (LeafField Leaf, bool Complement)? LeafIn(Field f, int id, bool negated)
        {
            if (f is LeafField l) return l.Surface.Id == id ? (l, l.Complement ^ negated) : null;
            foreach (var c in Children(f))
                if (LeafIn(c, id, negated ^ f is NegateField) is { } hit) return hit;
            return null;
        }

        /// <summary>A face's exact surface: its own, or for a fillet face the one candidate its mesh lies on.</summary>
        Surface Exact(Surface owner, int face) =>
            owner.Kind != SurfaceKind.Fillet ? owner : Candidates(owner, _points[_meshVertices[face].First()]).MinBy(s => Deviation(s, face)) ?? owner;

        /// <summary>
        /// What a fillet face can be: a rolling ball between two surfaces (a cylinder or torus), a loop's chamfer
        /// (a plane or cone), or, inside a shell, either of those moved by the wall thickness.
        /// </summary>
        IEnumerable<Surface> Candidates(Surface owner, Point3D near)
        {
            if (_rounded.TryGetValue(owner.Id, out var r) && Rolled(r.A.Surface, r.A.Complement, r.B.Surface, r.B.Complement, r.Radius, owner, near) is { } rolled)
                yield return rolled;
            if (_loops.TryGetValue(owner.Id, out var l)
                && (l.Loop.Round ? Rolled(l.Loop.Cap.Surface, l.Loop.Cap.Complement, l.Side.Surface, l.SideComplement, l.Loop.Size, owner, near)
                    : Chamfer(l.Loop.Cap, l.Side.Surface, l.SideComplement, l.Loop.Size, owner)) is { } loop)
                yield return loop;
            if (owner.Source.IndexOf("/inner:", StringComparison.Ordinal) is var at and > 0)
            {
                var outerSource = owner.Source[(at + "/inner:".Length)..];
                foreach (var outer in result.Part.Surfaces.Where(s => s.Source == outerSource && s.Kind == SurfaceKind.Fillet && s.Id != owner.Id))
                foreach (var exact in Candidates(outer, near))
                foreach (var t in _shells)
                {
                    yield return exact.Offset(owner.Id, owner.Source, t);
                    yield return exact.Offset(owner.Id, owner.Source, -t);
                }
            }
        }

        /// <summary>
        /// A rolling-ball fillet between two surfaces is the tube round the ball centres' path, which is where the two
        /// surfaces offset inward by the radius meet: a line for two planes (a cylinder), a circle for a plane across
        /// a cylinder (a torus). Other pairs sweep no closed-form surface; null.
        /// </summary>
        static Surface? Rolled(Surface a, bool ca, Surface b, bool cb, double radius, Surface fillet, Point3D near)
        {
            static Surface Inward(Surface s, bool complement, double r) => s.Offset(0, "", complement ? r : -r);
            var path = SurfaceIntersection.Curve(Inward(a, ca, radius), Inward(b, cb, radius), near);
            return path switch
            {
                Line l => fillet with { Kind = SurfaceKind.Cylinder, Origin = l.Origin, Axis = l.Direction, Radius = radius },
                Circle c => fillet with { Kind = SurfaceKind.Torus, Origin = c.Center, Axis = c.Normal, Radius = c.Radius, Minor = radius },
                _ => null,
            };
        }

        /// <summary>
        /// A loop chamfer is where cap + side + d = 0 for the cap's and side's distances (outward from the loop's
        /// material): a plane along a flat side, a 45° cone round a cylinder whose axis is the cap's normal.
        /// </summary>
        static Surface? Chamfer(LeafField cap, Surface side, bool sideComplement, double d, Surface face)
        {
            var na = cap.Complement ? -cap.Surface.Axis : cap.Surface.Axis;
            if (side.Kind == SurfaceKind.Plane)
            {
                var n = na + (sideComplement ? -side.Axis : side.Axis);
                var value = (sideComplement ? -side.Axis : side.Axis).Dot(cap.Surface.Origin - side.Origin) + d;
                return face with { Kind = SurfaceKind.Plane, Origin = cap.Surface.Origin - n * (value / n.LengthSquared), Axis = n.Normalized() };
            }
            if (side.Kind != SurfaceKind.Cylinder) return null;
            // In (ρ, z) along the side's axis: cap = s·(z − z0), side = ±(ρ − R); their sum + d is a 45° line.
            var s = na.Dot(side.Axis) > 0 ? 1 : -1;
            var z0 = side.Axis.Dot(cap.Surface.Origin - side.Origin);
            var (apex, opening) = sideComplement ? (z0 - s * (side.Radius + d), s) : (z0 + s * (side.Radius - d), -s);
            return face with { Kind = SurfaceKind.Cone, Origin = side.Origin + side.Axis * apex, Axis = side.Axis * opening, Angle = Math.PI / 4 };
        }

        /// <summary>
        /// Two faces of one loop whose side faces run into each other tangentially at a profile corner (a line into an
        /// arc, or one arc split in two) do not meet in a surface intersection: their edge lies in the plane across the
        /// junction, a quarter circle round the ball centre for a fillet, a straight cut for a chamfer. With its ends
        /// on the cap and on the side; null for any other pair.
        /// </summary>
        (Curve Curve, Point3D OnCap, Point3D OnSide)? Junction(int a, int b, Point3D near)
        {
            if (!_loops.TryGetValue(_owner[a].Id, out var la) || !_loops.TryGetValue(_owner[b].Id, out var lb)
                || la.Loop != lb.Loop || la.Profile is null || la.Profile != lb.Profile)
                return null;
            var junctions = la.Profile.TangentJunctions(la.Side, lb.Side).ToList();
            if (junctions.Count == 0) return null;
            // The junction points lie on the sketch plane: the nearest across the extrude direction.
            var j = junctions.MinBy(p => (p - near).LengthSquared - Math.Pow(la.Loop.Cap.Surface.Axis.Dot(p - near), 2));
            var cap = la.Loop.Cap;
            var na = cap.Complement ? -cap.Surface.Axis : cap.Surface.Axis;
            var onEdge = j - na * na.Dot(j - cap.Surface.Origin);
            var nb = la.Side.Surface.Gradient(onEdge) * (la.SideComplement ? -1 : 1);
            var size = la.Loop.Size;
            var (p, q) = (onEdge - nb * size, onEdge - na * size);
            if (!la.Loop.Round) return (new Line(p, (q - p).Normalized()), p, q);
            var centre = onEdge - (na + nb) * size;
            return (new Circle(centre, na.Cross(nb).Normalized(), (p - centre).Normalized(), size), p, q);
        }

        /// <summary>Closed walks through a face's boundary half-edges; at a pinch the walk stays with the same neighbour.</summary>
        static List<List<(int From, int To, int Other)>> Cycles(List<(int From, int To, int Other)> halves)
        {
            var outgoing = new Dictionary<int, List<int>>();
            for (var i = 0; i < halves.Count; i++)
            {
                if (!outgoing.TryGetValue(halves[i].From, out var list)) outgoing[halves[i].From] = list = [];
                list.Add(i);
            }
            var used = new bool[halves.Count];
            var cycles = new List<List<(int, int, int)>>();
            for (var start = 0; start < halves.Count; start++)
            {
                if (used[start]) continue;
                var cycle = new List<(int, int, int)>();
                for (var cur = start; cur >= 0;)
                {
                    used[cur] = true;
                    cycle.Add(halves[cur]);
                    var next = outgoing[halves[cur].To].Where(i => !used[i]).OrderBy(i => halves[i].Other == halves[cur].Other ? 0 : 1).DefaultIfEmpty(-1).First();
                    cur = next;
                }
                cycles.Add(cycle);
            }
            return cycles;
        }

        (BrepLoop Loop, List<Point3D> Points) Loop(int face, List<(int From, int To, int Other)> cycle)
        {
            var points = cycle.Select(h => _points[h.From]).ToList();

            // Split the walk where the neighbour changes; a walk with one neighbour throughout is a closed edge.
            var n = cycle.Count;
            var first = Enumerable.Range(0, n).FirstOrDefault(i => cycle[i].Other != cycle[(i + n - 1) % n].Other, -1);
            if (first < 0)
            {
                var whole = new Run(cycle[0].Other, cycle.Select(h => h.From).Append(cycle[0].From).ToList());
                return (new BrepLoop([ClosedEdge(face, whole)], false), points);
            }
            var runs = new List<Run>();
            for (var k = 0; k < n; k++)
            {
                var h = cycle[(first + k) % n];
                if (runs.Count == 0 || runs[^1].Other != h.Other) runs.Add(new Run(h.Other, [h.From]));
                runs[^1].Vertices.Add(h.To);
            }

            // The corner between two runs lies on this face and both neighbours. Where more than three faces meet the
            // mesh may leave a run between two corners that are one vertex: no edge.
            var corners = runs.Select((r, i) => VertexAt(r.Vertices[0], [face, r.Other, runs[(i + runs.Count - 1) % runs.Count].Other])).ToList();
            var kept = Enumerable.Range(0, runs.Count).Where(i => corners[i] != corners[(i + 1) % runs.Count]).ToList();
            var coedges = kept.Select(i => OpenEdge(face, runs[i], corners[i], corners[(i + 1) % runs.Count])).ToList();
            return (new BrepLoop(coedges, false), points);
        }

        BrepCoedge OpenEdge(int face, Run run, int start, int end)
        {
            if (_edgeOfKey.TryGetValue(Key(run), out var known)) return new BrepCoedge(known, false);
            var curve = CurveOf(face, run);
            var (p0, p1) = (_vertices[start].Point, _vertices[end].Point);
            curve = curve switch
            {
                Line l => new Line(p0, (p1 - p0).Dot(l.Direction) >= 0 ? l.Direction : -l.Direction),
                _ => Winding(curve, run),
            };
            return new BrepCoedge(AddEdge(run, curve, start, end), true);
        }

        BrepCoedge ClosedEdge(int face, Run run)
        {
            if (_edgeOfKey.TryGetValue(Key(run), out var known)) return new BrepCoedge(known, false);
            var curve = Winding(CurveOf(face, run), run);
            if (curve is Line) throw new NotSupportedException("a closed straight edge");
            return new BrepCoedge(AddEdge(run, curve, -1, -1), true);
        }

        /// <summary>
        /// Seams for faces whose boundary runs round their surface's axis, so that every loop is closed in the face's
        /// parameters. A band (two loops round the axis) is cut along one generator or meridian from a vertex of one
        /// loop to a vertex of the other; a sphere cap from its rim to the pole; a whole torus along both circles. A
        /// loop's corners are where they are, but the vertex of a closed edge can go anywhere on it, so bands with a
        /// corner go first and pass the angle of their seam on through their closed edges; the rest take angle 0.
        /// A sphere face within a hemisphere instead gets its poles turned away, and needs no seam at all. Faces whose
        /// loops have no vertices at a common angle are left for the reader to cut.
        /// </summary>
        void Seams(List<BrepLoop>[] loops, List<List<Point3D>>[] points, bool[] outward)
        {
            var work = new List<(int Face, int A, int B)>(); // B = −1: a cap, seamed to the pole
            for (var f = 0; f < loops.Length; f++)
            {
                var s = _surface[f];
                if (s.Kind == SurfaceKind.Plane) continue;
                if (s.Kind == SurfaceKind.Sphere && PoleFree(f) is { } axis)
                {
                    _surface[f] = s with { Axis = axis };
                    continue;
                }
                if (loops[f].Count == 0)
                {
                    if (s.Kind == SurfaceKind.Torus) loops[f].Add(WholeTorus(f, outward[f]));
                    continue;
                }
                var windings = points[f].Select(p => Winding(s, p)).ToList();
                if (windings.Any(w => w.V != 0)) continue;
                var round = Enumerable.Range(0, windings.Count).Where(i => windings[i].U != 0).ToList();
                if (round.Count == 2) work.Add((f, round[0], round[1]));
                else if (round.Count == 1 && s.Kind == SurfaceKind.Sphere) work.Add((f, round[0], -1));
            }

            bool Fixed(BrepLoop l) => l.Coedges.All(c => _edges[c.Edge].Start >= 0);
            while (work.Count > 0)
            {
                var item = work.FirstOrDefault(w => Fixed(loops[w.Face][w.A]) || w.B >= 0 && Fixed(loops[w.Face][w.B]), work[0]);
                work.Remove(item);
                var (f, a, b) = item;
                var s = _surface[f];
                var (la, lb) = (loops[f][a], b >= 0 ? loops[f][b] : null);

                // The seam's angle: at a corner of either loop where the other has one too, else free.
                (int A, int B)? ends = null;
                var anglesA = Fixed(la) ? Starts(la).Select(v => (v, U(s, v))).ToList() : null;
                var anglesB = lb is not null && Fixed(lb) ? Starts(lb).Select(v => (v, U(s, v))).ToList() : null;
                if (lb is null)
                {
                    var va = anglesA?[0].v ?? Place(la, s, 0);
                    // A cap's seam ends at the pole on the face's side of its rim.
                    var rim = points[f][a];
                    var centre = new Point3D(rim.Average(p => p.X), rim.Average(p => p.Y), rim.Average(p => p.Z));
                    if (va >= 0) ends = (va, AddVertex(s.Origin + s.Axis * (Math.Sign((_inside[f] - centre).Dot(s.Axis)) * s.Radius)));
                }
                else if (anglesA is not null && anglesB is not null)
                {
                    foreach (var (va, ua) in anglesA)
                        if (anglesB.Where(x => Math.Abs(Angle(x.Item2 - ua)) <= 1e-9).Select(x => (int?)x.v).FirstOrDefault() is { } vb)
                        {
                            ends = (va, vb);
                            break;
                        }
                }
                else
                {
                    var u = anglesA?[0].Item2 ?? anglesB?[0].Item2 ?? 0;
                    var (va, vb) = (anglesA?[0].v ?? Place(la, s, u), anglesB?[0].v ?? Place(lb!, s, u));
                    if (va >= 0 && vb >= 0) ends = (va, vb);
                }
                if (ends is not { } e) continue;

                _edges.Add((Seam(f, _vertices[e.A].Point, _vertices[e.B].Point), e.A, e.B));
                var seam = _edges.Count - 1;
                List<BrepCoedge> joined = [.. Rotated(la, e.A), new BrepCoedge(seam, true)];
                if (lb is not null) joined.AddRange(Rotated(lb, e.B));
                joined.Add(new BrepCoedge(seam, false));
                var merged = new BrepLoop(joined, true);
                loops[f] = loops[f].Where((_, i) => i != a && i != b).Prepend(merged).ToList();
                points[f] = points[f].Where((_, i) => i != a && i != b).Prepend(new List<Point3D>()).ToList();
            }
        }

        List<int> Starts(BrepLoop l) => l.Coedges.Select(c => c.Forward ? _edges[c.Edge].Start : _edges[c.Edge].End).ToList();

        IEnumerable<BrepCoedge> Rotated(BrepLoop l, int start)
        {
            var k = Starts(l).IndexOf(start);
            return l.Coedges.Skip(k).Concat(l.Coedges.Take(k));
        }

        double U(Surface s, int vertex) => SurfaceParameters.Of(s, _vertices[vertex].Point).U;

        /// <summary>An angle difference brought into (−π, π].</summary>
        static double Angle(double a) => a - SurfaceParameters.Turns(a);

        /// <summary>
        /// Puts a loop's closed edge vertex where the curve crosses the half-plane at angle <paramref name="u"/> round
        /// the surface's axis; −1 if the loop has corners or the curve does not cross there.
        /// </summary>
        int Place(BrepLoop loop, Surface s, double u)
        {
            if (loop.Coedges.Count != 1 || _edges[loop.Coedges[0].Edge] is not { Start: < 0 } edge) return -1;
            var (x, y, z) = SurfaceParameters.Axes(s);
            var radial = x * Math.Cos(u) + y * Math.Sin(u);
            var cut = new Surface(0, "", SurfaceKind.Plane, s.Origin, z.Cross(radial));
            var hit = Enumerable.Range(0, 8).Select(k => SurfaceIntersection.Cross(edge.Curve, cut, edge.Curve.At(k * Math.PI / 4)))
                .FirstOrDefault(h => h is { } c && (c.Point - s.Origin).Dot(radial) > 0);
            if (hit is not { } found) return -1;
            var v = AddVertex(found.Point);
            _edges[loop.Coedges[0].Edge] = edge with { Start = v, End = v };
            return v;
        }

        /// <summary>
        /// The seam from p to q at their common angle: a generator of a cylinder or cone, or the meridian circle of a
        /// sphere or torus, turned so that its arc from p to q crosses the face.
        /// </summary>
        Curve Seam(int face, Point3D p, Point3D q)
        {
            var s = _surface[face];
            if (s.Kind is SurfaceKind.Cylinder or SurfaceKind.Cone) return new Line(p, (q - p).Normalized());
            var (x, y, z) = SurfaceParameters.Axes(s);
            var u = SurfaceParameters.Of(s, p).U;
            var radial = x * Math.Cos(u) + y * Math.Sin(u);
            var torus = s.Kind == SurfaceKind.Torus;
            var centre = s.Origin + radial * (torus ? s.Radius : 0);
            var circle = new Circle(centre, radial.Cross(z), (p - centre).Normalized(), torus ? s.Minor : s.Radius);
            var v = _inside[face] - s.Origin;
            var height = z.Dot(v);
            var onMeridian = s.Origin + radial * (v - z * height).Length + z * height;
            double Turn(Circle c, Point3D at) => (c.Parameter(at) + 2 * Math.PI) % (2 * Math.PI);
            return Turn(circle, onMeridian) < Turn(circle, q) ? circle : circle with { Normal = -circle.Normal };
        }

        /// <summary>How often a loop's mesh points run round the surface's axis (u) and, on a torus, round its tube (v).</summary>
        static (int U, int V) Winding(Surface s, List<Point3D> loop)
        {
            var (du, dv) = (0.0, 0.0);
            (double U, double V)? last = null;
            foreach (var p in loop.Append(loop[0]))
            {
                var uv = SurfaceParameters.Of(s, p);
                if (double.IsNaN(uv.U)) continue;
                if (last is { } l)
                {
                    du += Angle(uv.U - l.U);
                    if (s.Kind == SurfaceKind.Torus) dv += Angle(uv.V - l.V);
                }
                last = uv;
            }
            return ((int)Math.Round(du / (2 * Math.PI)), (int)Math.Round(dv / (2 * Math.PI)));
        }

        /// <summary>For a sphere face within less than a hemisphere: an axis whose poles lie outside it.</summary>
        Vector3D? PoleFree(int face)
        {
            var s = _surface[face];
            var directions = _meshVertices[face].Select(v => (_points[v] - s.Origin).Normalized()).ToList();
            var sum = directions.Aggregate(new Vector3D(0, 0, 0), (a, d) => a + d);
            if (sum.Length < 1e-9) return null;
            var centre = sum.Normalized();
            // Every point of the face within 87° of its centre direction: the poles of an axis across it are clear.
            return directions.All(d => d.Dot(centre) > Math.Sin(0.05)) ? SurfaceIntersection.Perpendicular(centre) : null;
        }

        /// <summary>A torus face with no boundary at all: one vertex, the circle round the axis and the tube circle as seams.</summary>
        BrepLoop WholeTorus(int face, bool outward)
        {
            var s = _surface[face];
            var (x, y, z) = SurfaceParameters.Axes(s);
            var v = AddVertex(s.Origin + x * (s.Radius + s.Minor));
            _edges.Add((new Circle(s.Origin, z, x, s.Radius + s.Minor), v, v));
            _edges.Add((new Circle(s.Origin + x * s.Radius, -y, x, s.Minor), v, v));
            var (round, tube) = (_edges.Count - 2, _edges.Count - 1);
            // Counter-clockwise in (u, v): along u, up v, back along u, down v. The parameter normal points out of the tube.
            List<BrepCoedge> loop = [new(round, true), new(tube, true), new(round, false), new(tube, false)];
            if (!outward) loop = loop.AsEnumerable().Reverse().Select(c => c with { Forward = !c.Forward }).ToList();
            return new BrepLoop(loop, true);
        }

        /// <summary>
        /// The final edge: its curve parametrised so that the edge starts at t = π/2 on a circle (on an ellipse in
        /// [π/2, 3π/2]), clear of the 0 / 2π wrap where readers would differ on which end of the range a vertex is.
        /// </summary>
        BrepEdge Finish(int i)
        {
            var (curve, start, end) = _edges[i];
            var (p, q) = (_vertices[start].Point, _vertices[end].Point);
            const double quarter = Math.PI / 2;
            static double Turn(double a) => a - 2 * Math.PI * Math.Floor(a / (2 * Math.PI));
            switch (curve)
            {
                case Line l:
                    return new BrepEdge(i, l with { Origin = p }, start, end, 0, (q - p).Dot(l.Direction));
                case Circle c:
                    var radial = p - c.Center;
                    var circle = c with { X = (radial - c.Normal * radial.Dot(c.Normal)).Normalized().Cross(c.Normal) };
                    return new BrepEdge(i, circle, start, end, quarter, quarter + (start == end ? 2 * Math.PI : Turn(circle.Parameter(q) - quarter)));
                case Ellipse e:
                    var t = Turn(e.Parameter(p));
                    if (t < quarter || t > 3 * quarter) (e, t) = (e with { X = -e.X }, Turn(t + Math.PI));
                    return new BrepEdge(i, e, start, end, t, t + (start == end ? 2 * Math.PI : Turn(e.Parameter(q) - t)));
                default:
                    throw new NotSupportedException(curve.GetType().Name);
            }
        }

        /// <summary>
        /// The loops of a curved face with every coedge's image in the face's parameters, each shifted by whole turns
        /// to start where the one before it ended.
        /// </summary>
        List<BrepLoop> WithImages(int face, List<BrepLoop> loops, List<BrepEdge> edges)
        {
            var s = _surface[face];
            if (s.Kind == SurfaceKind.Plane) return loops;
            var images = new Dictionary<int, UvCurve>();
            (double U, double V)? anchor = null;
            var result = new List<BrepLoop>();
            foreach (var loop in loops)
            {
                var coedges = new List<BrepCoedge>();
                var last = anchor;
                foreach (var c in loop.Coedges)
                {
                    var e = edges[c.Edge];
                    if (!images.TryGetValue(c.Edge, out var image)) images[c.Edge] = image = SurfaceParameters.Image(s, e.Curve, e.T0, e.T1);
                    var from = c.Forward ? image.Start : image.End;
                    if (last is { } to)
                        image = image.Shifted(SurfaceParameters.PeriodicU(s) ? SurfaceParameters.Turns(to.U - from.U) : 0,
                            SurfaceParameters.PeriodicV(s) ? SurfaceParameters.Turns(to.V - from.V) : 0);
                    coedges.Add(c with { Uv = image });
                    anchor ??= c.Forward ? image.Start : image.End;
                    last = c.Forward ? image.End : image.Start;
                }
                result.Add(loop with { Coedges = coedges });
            }
            return result;
        }

        /// <summary>
        /// Each face's shell (faces joined by edges) and whether that shell is a void: one enclosing negative volume,
        /// a cavity seen from the material.
        /// </summary>
        (int[] ShellOf, bool[] IsVoid) Shells(List<BrepLoop>[] loops, Meshing.Mesh mesh, IReadOnlyList<int> faceOfQuad)
        {
            var group = Enumerable.Range(0, loops.Length).ToArray();
            int Find(int f) => group[f] == f ? f : group[f] = Find(group[f]);
            var faceOfEdge = new Dictionary<int, int>();
            for (var f = 0; f < loops.Length; f++)
            foreach (var c in loops[f].SelectMany(l => l.Coedges))
                if (!faceOfEdge.TryAdd(c.Edge, f)) group[Find(f)] = Find(faceOfEdge[c.Edge]);
            var volume = new double[loops.Length];
            for (var q = 0; q < mesh.Quads.Count; q++)
            {
                var quad = mesh.Quads[q];
                var (a, b, c, d) = (_points[quad.A].AsVector(), _points[quad.B].AsVector(), _points[quad.C].AsVector(), _points[quad.D].AsVector());
                volume[Find(faceOfQuad[q])] += (a.Dot(b.Cross(c)) + a.Dot(c.Cross(d))) / 6;
            }
            var shellOf = Enumerable.Range(0, loops.Length).Select(Find).ToArray();
            return (shellOf, shellOf.Select(s => volume[s] <= 0).ToArray());
        }

        /// <summary>One solid per body: its outer shell, and its voids.</summary>
        List<BrepSolid> Solids(List<BrepFace> faces, int[] shellOf, bool[] isVoid)
        {
            var topology = result.Topology!;
            var solids = new List<BrepSolid>();
            foreach (var body in faces.GroupBy(f => topology.Faces[f.TopologyFace].Body).OrderBy(g => g.Key))
            {
                var name = result.Bodies.FirstOrDefault(b => b.Faces.Contains(body.First().TopologyFace))?.Name ?? $"body{body.Key}";
                var shells = body.GroupBy(f => shellOf[f.TopologyFace]).ToList();
                var outer = shells.Where(s => !isVoid[s.Key]).Select(s => new BrepShell(s.ToList())).ToList();
                var voids = shells.Where(s => isVoid[s.Key]).Select(s => new BrepShell(s.ToList())).ToList();
                if (outer.Count != 1) throw new NotSupportedException($"body {name} has {outer.Count} outer shells");
                solids.Add(new BrepSolid(name, outer[0], voids));
            }
            return solids;
        }

        int AddEdge(Run run, Curve curve, int start, int end)
        {
            _edges.Add((curve, start, end));
            _edgeOfKey[Key(run)] = _edges.Count - 1;
            return _edges.Count - 1;
        }

        Curve CurveOf(int face, Run run)
        {
            var seed = _points[run.Vertices[run.Vertices.Count / 2]];
            if (Junction(face, run.Other, seed) is { } j) return j.Curve;
            return SurfaceIntersection.Curve(_surface[face], _surface[run.Other], seed)
                ?? throw new NotSupportedException($"no closed-form edge between {Name(face)} and {Name(run.Other)} ({_surface[face].Kind}–{_surface[run.Other].Kind})");
        }

        /// <summary>A circle or ellipse turned so that it runs the way the mesh walks it.</summary>
        Curve Winding(Curve curve, Run run)
        {
            var (centre, normal) = curve switch
            {
                Circle c => (c.Center, c.Normal),
                Ellipse e => (e.Center, e.Normal),
                _ => throw new NotSupportedException(curve.GetType().Name),
            };
            var turn = 0.0;
            for (var i = 0; i + 1 < run.Vertices.Count; i++)
            {
                var (p, q) = (_points[run.Vertices[i]], _points[run.Vertices[i + 1]]);
                turn += (p - centre).Cross(q - p).Dot(normal);
            }
            if (turn >= 0) return curve;
            return curve switch
            {
                Circle c => c with { Normal = -c.Normal },
                Ellipse e => e with { Normal = -e.Normal },
                _ => curve,
            };
        }

        /// <summary>
        /// The exact vertex at a mesh corner. A tangent junction of a loop knows its ends. Otherwise, of every edge
        /// curve between two faces there, its crossing with a third face's surface, and of two straight edges their
        /// crossing, taking the most transversal one: a tangent edge's neighbours cross it at a grazing angle, and
        /// two fillets mitring on their cap both touch it, so only their lines cross it cleanly.
        /// </summary>
        int VertexAt(int meshVertex, int[] known)
        {
            if (_vertexOfMesh.TryGetValue(meshVertex, out var v)) return v;
            var seed = _points[meshVertex];
            var faces = known.Concat(_facesAt[meshVertex]).Distinct().ToList();
            var reach = 4 * settings.CellSize;
            for (var i = 0; i < faces.Count; i++)
            for (var j = i + 1; j < faces.Count; j++)
                if (Junction(faces[i], faces[j], seed) is { } junction)
                {
                    var end = (junction.OnCap - seed).Length <= (junction.OnSide - seed).Length ? junction.OnCap : junction.OnSide;
                    if ((end - seed).Length <= reach) return _vertexOfMesh[meshVertex] = AddVertex(end);
                }

            (Point3D Point, double Transversality)? best = null;
            var lines = new List<Line>();
            for (var i = 0; i < faces.Count; i++)
            for (var j = i + 1; j < faces.Count; j++)
            {
                if (SurfaceIntersection.Curve(_surface[faces[i]], _surface[faces[j]], seed) is not { } curve) continue;
                if (curve is Line line) lines.Add(line);
                foreach (var k in faces)
                    if (k != faces[i] && k != faces[j] && SurfaceIntersection.Cross(curve, _surface[k], seed) is { } hit
                        && (hit.Point - seed).Length <= reach && (best is null || hit.Transversality > best.Value.Transversality))
                        best = hit;
            }
            for (var i = 0; i < lines.Count; i++)
            for (var j = i + 1; j < lines.Count; j++)
                if (Crossing(lines[i], lines[j]) is { } hit && (hit.Point - seed).Length <= reach && (best is null || hit.Transversality > best.Value.Transversality))
                    best = hit;
            if (best is null) throw new NotSupportedException($"no closed-form vertex of {string.Join(", ", faces.Select(Name))}");
            return _vertexOfMesh[meshVertex] = AddVertex(best.Value.Point);
        }

        /// <summary>Where two lines meet, and the sine of their angle; null if they are parallel or pass apart.</summary>
        static (Point3D Point, double Transversality)? Crossing(Line a, Line b)
        {
            var c = a.Direction.Dot(b.Direction);
            var sin2 = 1 - c * c;
            if (sin2 < 1e-12) return null;
            var w = a.Origin - b.Origin;
            var (da, db) = (a.Direction.Dot(w), b.Direction.Dot(w));
            var (p, q) = (a.At((c * db - da) / sin2), b.At((db - c * da) / sin2));
            return (p - q).Length > 1e-9 ? null : (p + (q - p) * 0.5, Math.Sqrt(sin2));
        }

        // Corners found from different mesh vertices (a corner spread over several cells) are one vertex.
        int AddVertex(Point3D p)
        {
            var same = _vertices.FindIndex(x => (x.Point - p).Length <= 1e-6);
            if (same >= 0) return same;
            _vertices.Add(new BrepVertex(_vertices.Count, p));
            return _vertices.Count - 1;
        }

        string Name(int face) => string.Join("+", result.Topology!.Faces[face].Sources);

        /// <summary>The run's lowest undirected mesh edge: the same from both faces.</summary>
        long Key(Run run)
        {
            var best = long.MaxValue;
            for (var i = 0; i + 1 < run.Vertices.Count; i++)
            {
                var (a, b) = (run.Vertices[i], run.Vertices[i + 1]);
                best = Math.Min(best, (long)Math.Min(a, b) * _points.Count + Math.Max(a, b));
            }
            return best;
        }

        static double SignedArea(List<Point3D> loop, Vector3D normal)
        {
            var sum = 0.0;
            for (var i = 0; i < loop.Count; i++)
                sum += loop[i].AsVector().Cross(loop[(i + 1) % loop.Count].AsVector()).Dot(normal);
            return sum / 2;
        }

        static IEnumerable<(int, int)> Sides(Meshing.Quad q)
        {
            yield return (q.A, q.B);
            yield return (q.B, q.C);
            if (!q.IsTriangle) yield return (q.C, q.D);
            yield return (q.D, q.A);
        }
    }
}
