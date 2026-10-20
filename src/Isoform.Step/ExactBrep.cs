using BasAutomation.Geometry;
using Isoform.Geometry;
using Isoform.Modeling;

namespace Isoform.Step;

/// <summary>
/// Turns an Isoform result into an exact B-rep. The mesh only supplies the combinatorics and the seeds: which faces
/// meet along which chains, in which order round each face. All geometry is exact: each face is its analytic
/// surface, each edge the closed-form intersection of its two surfaces, each vertex the point where an edge curve
/// crosses a third surface. Loops come from the mesh boundary of each face, whose quads wind counter-clockwise seen
/// from outside, so every loop already runs with its face on the left.
/// </summary>
public static class ExactBrep
{
    /// <summary>Builds the B-rep of every body. Throws <see cref="NotSupportedException"/> naming the faces or edges without a closed form.</summary>
    public static Brep Build(FrepResult result, ModelingSettings settings) => new Builder(result, settings).Run();

    /// <summary>One face's boundary as the mesh walks it: the face across each mesh edge, and the mesh vertices.</summary>
    sealed record Run(int Other, List<int> Vertices);

    sealed class Builder(FrepResult result, ModelingSettings settings)
    {
        readonly IReadOnlyList<Point3D> _points = result.Mesh!.Vertices;
        readonly List<BrepVertex> _vertices = [];
        readonly List<BrepEdge> _edges = [];
        readonly Dictionary<long, int> _edgeOfKey = [];
        readonly Dictionary<int, int> _vertexOfMesh = [];
        readonly Dictionary<int, HashSet<int>> _facesAt = [];
        Surface[] _surface = [];

        // A point inside each face (a quad centre), to tell which way a seam runs.
        Point3D[] _inside = [];

        public Brep Run()
        {
            var mesh = result.Mesh!;
            var topology = result.Topology!;
            var faceOfQuad = result.FaceOfQuad;
            var faceCount = topology.Faces.Count;

            // Each face's surface, and whether its gradient points out of the solid (the quads wind outward).
            _surface = new Surface[faceCount];
            _inside = new Point3D[faceCount];
            var sense = new double[faceCount];
            var fillets = result.Part.Field is null ? [] : FieldAlgebra.Descendants(result.Part.Field).OfType<RoundedEdgeField>()
                .GroupBy(r => r.Fillet.Id).ToDictionary(g => g.Key, g => g.First());
            for (var q = 0; q < mesh.Quads.Count; q++)
                if (_surface[faceOfQuad[q]] is null)
                {
                    var owner = result.Part.Surfaces[mesh.Quads[q].Owner];
                    _surface[faceOfQuad[q]] = fillets.TryGetValue(owner.Id, out var r) ? Rolled(r, _points[mesh.Quads[q].A]) ?? owner : owner;
                }
            var unsupported = Enumerable.Range(0, faceCount).Where(f => _surface[f].Kind is SurfaceKind.Fillet or SurfaceKind.Blend)
                .Select(f => $"{string.Join("+", topology.Faces[f].Sources)} ({_surface[f].Kind})").ToList();
            if (unsupported.Count > 0) throw new NotSupportedException($"faces without a closed-form surface: {string.Join(", ", unsupported)}");
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

            var faces = new BrepFace[faceCount];
            for (var f = 0; f < faceCount; f++)
            {
                var outward = sense[f] > 0;
                var loops = Cycles(halves[f]).Select(cycle => Loop(f, cycle)).ToList();
                var planar = _surface[f].Kind == SurfaceKind.Plane;
                faces[f] = new BrepFace(f, topology.Faces[f].Sources, _surface[f], outward, WithSeam(f, loops.Select(l => l.Loop with
                {
                    Outer = loops.Count == 1 || planar && SignedArea(l.Points, _surface[f].Axis * (outward ? 1 : -1)) > 0,
                }).ToList()));
            }

            var solids = faces.GroupBy(f => topology.Faces[f.TopologyFace].Body).OrderBy(g => g.Key)
                .Select(g => new BrepSolid(result.Bodies.FirstOrDefault(b => b.Faces.Contains(g.First().TopologyFace))?.Name ?? $"body{g.Key}", g.ToList()))
                .ToList();
            return new Brep(_vertices, _edges, solids);
        }

        /// <summary>
        /// A rolling-ball fillet between two surfaces is the tube round the ball centres' path, which is where the two
        /// surfaces offset inward by the radius meet: a line for two planes (a cylinder), a circle for a plane across
        /// a cylinder (a torus). Other pairs sweep no closed-form surface; null.
        /// </summary>
        static Surface? Rolled(RoundedEdgeField r, Point3D near)
        {
            static Surface Inward(LeafField leaf, double radius) => leaf.Surface.Offset(0, "", leaf.Complement ? radius : -radius);
            var path = SurfaceIntersection.Curve(Inward(r.A, r.Radius), Inward(r.B, r.Radius), near);
            return path switch
            {
                Line l => r.Fillet with { Kind = SurfaceKind.Cylinder, Origin = l.Origin, Axis = l.Direction, Radius = r.Radius },
                Circle c => r.Fillet with { Kind = SurfaceKind.Torus, Origin = c.Center, Axis = c.Normal, Radius = c.Radius, Minor = r.Radius },
                _ => null,
            };
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

            // The corner between two runs lies on this face and both neighbours.
            var corners = runs.Select((r, i) => VertexAt(r.Vertices[0], [face, r.Other, runs[(i + runs.Count - 1) % runs.Count].Other])).ToList();
            var coedges = runs.Select((r, i) => OpenEdge(face, r, corners[i], corners[(i + 1) % runs.Count])).ToList();
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
            var periodic = new[] { _surface[face], _surface[run.Other] }.FirstOrDefault(s => s.Kind is not SurfaceKind.Plane);
            var vertex = AddVertex(periodic is null ? curve.At(0) : OnSeam(curve, periodic));
            return new BrepCoedge(AddEdge(run, curve, vertex, vertex), true);
        }

        /// <summary>
        /// Where a closed curve crosses the periodic surface's u = 0 half-plane (its placement's x side, see
        /// <see cref="SurfaceIntersection.Perpendicular"/>): every closed edge of a band then has its vertex on one
        /// generator, which the seam joins.
        /// </summary>
        static Point3D OnSeam(Curve curve, Surface periodic)
        {
            var x = SurfaceIntersection.Perpendicular(periodic.Axis);
            var cut = new Surface(0, "", SurfaceKind.Plane, periodic.Origin, periodic.Axis.Cross(x));
            return Enumerable.Range(0, 4).Select(k => SurfaceIntersection.Cross(curve, cut, curve.At(k * Math.PI / 2)))
                .Where(h => h is { } hit && (hit.Point - periodic.Origin).Dot(x) > 0)
                .Select(h => h!.Value.Point).First();
        }

        /// <summary>
        /// A periodic band bounded by two closed edges, or a sphere cap bounded by one, gets the seam OCCT and most
        /// readers expect, walked there and back in one loop: on the u = 0 meridian from one closed edge's vertex to
        /// the other's, or to the pole. Bands whose boundaries have corners are left to the reader to cut open.
        /// </summary>
        List<BrepLoop> WithSeam(int face, List<BrepLoop> loops)
        {
            var s = _surface[face];
            if (s.Kind == SurfaceKind.Plane || loops.Count == 0 || loops.Any(l => l.Coedges.Count != 1 || !_edges[l.Coedges[0].Edge].IsClosed)) return loops;
            var rim = _edges[loops[0].Coedges[0].Edge].Curve as Circle;
            var cap = s.Kind == SurfaceKind.Sphere && loops.Count == 1 && rim is not null && rim.Normal.Cross(s.Axis).Length <= 1e-9;
            if (loops.Count != 2 && !cap) return loops;

            var from = _edges[loops[0].Coedges[0].Edge].Start;
            // A cap's seam ends at the pole on the face's side of its rim.
            var to = cap
                ? AddVertex(s.Origin + s.Axis * (Math.Sign((_inside[face] - rim!.Center).Dot(s.Axis)) * s.Radius))
                : _edges[loops[1].Coedges[0].Edge].Start;
            var (p, q) = (_vertices[from].Point, _vertices[to].Point);

            Curve seam;
            if (s.Kind is SurfaceKind.Cylinder or SurfaceKind.Cone) seam = new Line(p, (q - p).Normalized());
            else
            {
                // The meridian circle through both ends, turned so that its arc from p to q crosses the face.
                var x = SurfaceIntersection.Perpendicular(s.Axis);
                var centre = s.Origin + x * (s.Kind == SurfaceKind.Torus ? s.Radius : 0);
                var circle = new Circle(centre, x.Cross(s.Axis), (p - centre).Normalized(), s.Kind == SurfaceKind.Torus ? s.Minor : s.Radius);
                var v = _inside[face] - s.Origin;
                var height = s.Axis.Dot(v);
                var onMeridian = s.Origin + x * (v - s.Axis * height).Length + s.Axis * height;
                double Turn(Circle c, Point3D at) => (c.Parameter(at) + 2 * Math.PI) % (2 * Math.PI);
                seam = Turn(circle, onMeridian) < Turn(circle, q) ? circle : circle with { Normal = -circle.Normal };
            }
            _edges.Add(new BrepEdge(_edges.Count, seam, from, to));
            var index = _edges.Count - 1;
            List<BrepCoedge> loop = [loops[0].Coedges[0], new BrepCoedge(index, true)];
            if (!cap) loop.Add(loops[1].Coedges[0]);
            loop.Add(new BrepCoedge(index, false));
            return [new BrepLoop(loop, true)];
        }

        int AddEdge(Run run, Curve curve, int start, int end)
        {
            _edges.Add(new BrepEdge(_edges.Count, curve, start, end));
            _edgeOfKey[Key(run)] = _edges.Count - 1;
            return _edges.Count - 1;
        }

        Curve CurveOf(int face, Run run)
        {
            var seed = _points[run.Vertices[run.Vertices.Count / 2]];
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
        /// The exact vertex at a mesh corner: of every edge curve between two faces there, its crossing with a third
        /// face's surface, taking the most transversal one (a tangent edge's neighbours cross it at a grazing angle).
        /// </summary>
        int VertexAt(int meshVertex, int[] known)
        {
            if (_vertexOfMesh.TryGetValue(meshVertex, out var v)) return v;
            var seed = _points[meshVertex];
            var faces = known.Concat(_facesAt[meshVertex]).Distinct().ToList();
            var reach = 4 * settings.CellSize;
            (Point3D Point, double Transversality)? best = null;
            for (var i = 0; i < faces.Count; i++)
            for (var j = i + 1; j < faces.Count; j++)
            {
                if (SurfaceIntersection.Curve(_surface[faces[i]], _surface[faces[j]], seed) is not { } curve) continue;
                foreach (var k in faces)
                    if (k != faces[i] && k != faces[j] && SurfaceIntersection.Cross(curve, _surface[k], seed) is { } hit
                        && (hit.Point - seed).Length <= reach && (best is null || hit.Transversality > best.Value.Transversality))
                        best = hit;
            }
            if (best is null) throw new NotSupportedException($"no closed-form vertex of {string.Join(", ", faces.Select(Name))}");
            return _vertexOfMesh[meshVertex] = AddVertex(best.Value.Point);
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
