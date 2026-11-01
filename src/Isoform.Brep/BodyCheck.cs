using BasAutomation.Geometry;

namespace Isoform.Brep;

/// <summary>What is wrong with a body.</summary>
public enum ProblemKind
{
    /// <summary>An edge not used exactly twice: an open shell, or more than two faces at an edge.</summary>
    EdgeUses,
    /// <summary>Both uses of an edge run the same way: the faces on either side are oriented inconsistently.</summary>
    EdgeSenses,
    /// <summary>An edge whose parameter range is empty, or whose curve ends are not at its vertices.</summary>
    EdgeEnds,
    /// <summary>A coedge that does not end at the vertex the next one starts from.</summary>
    LoopBroken,
    /// <summary>A p-curve whose image on the face's surface is off the edge's curve by more than its tolerance.</summary>
    PcurveOffEdge,
    /// <summary>Consecutive p-curves that do not join in (u, v), such as a seam taken on the wrong side.</summary>
    LoopOpenInUv,
    /// <summary>An outer loop running clockwise or a hole counter-clockwise, seen from outside the solid.</summary>
    FaceOrientation,
    /// <summary>A hole loop that is not inside its face's outer loop.</summary>
    HoleOutside,
    /// <summary>A shell whose faces are not one connected piece.</summary>
    ShellDisconnected,
    /// <summary>A shell whose counts do not satisfy the Euler–Poincaré formula for any genus.</summary>
    ShellEuler,
    /// <summary>The outer shell enclosing negative volume, or a void positive volume: the shell is inside out.</summary>
    ShellOrientation,
}

/// <summary>One problem, with the entity it is on ("edge 3", "face 2 loop 1").</summary>
public sealed record Problem(ProblemKind Kind, string Where, string Message);

/// <summary>
/// Checks that a body is a valid closed, orientable, manifold solid: topology first (uses, senses, loops, shells,
/// Euler–Poincaré), then geometry against each entity's tolerance (curve ends, p-curves, (u, v) loops, orientation,
/// enclosed volume). Not yet checked: faces or edges crossing each other away from shared edges.
/// </summary>
public static class BodyCheck
{
    /// <summary>The points each edge and p-curve is sampled at, including both ends.</summary>
    const int Samples = 9;

    /// <summary>
    /// What is wrong with the body. Faces in <paramref name="unchanged"/> came whole from a valid body: their loops are
    /// not checked again, and a shell holding one faces the way it did (its edges' uses run opposite, which is checked),
    /// so its volume need not be found.
    /// </summary>
    public static IReadOnlyList<Problem> Problems(Body body, IReadOnlySet<int>? unchanged = null, IReadOnlySet<int>? loops = null)
    {
        bool Known(FaceId f) => unchanged?.Contains(f.Index) == true;
        // Loops of a valid body carried whole into a face that changed.
        bool KnownLoop(LoopId l) => loops?.Contains(l.Index) == true;
        var problems = new List<Problem>();
        void Add(ProblemKind kind, string where, string message) => problems.Add(new Problem(kind, where, message));

        for (var e = 0; e < body.Edges.Length; e++)
        {
            var edge = body.Edges[e];
            var uses = body.UsesOf(new EdgeId(e));
            if (unchanged is not null && uses.Length > 0 && uses.All(u => Known(body.FaceOf(u)) || KnownLoop(body[u].Loop)) &&
                (edge.Curve is Pole ? uses.Length == 1 : uses.Length == 2 && body[uses[0]].Forward != body[uses[1]].Forward))
                continue;
            var pole = edge.Curve is Pole;
            if (uses.Length != (pole ? 1 : 2)) Add(ProblemKind.EdgeUses, $"edge {e}", $"used {uses.Length} times, a closed manifold shell uses every edge twice (a pole's once)");
            else if (!pole && body[uses[0]].Forward == body[uses[1]].Forward) Add(ProblemKind.EdgeSenses, $"edge {e}", "both uses run the same way");

            if (!(edge.T1 > edge.T0)) Add(ProblemKind.EdgeEnds, $"edge {e}", $"empty parameter range [{edge.T0}, {edge.T1}]");
            CheckEnd(e, edge.Start, edge.T0, "start");
            CheckEnd(e, edge.End, edge.T1, "end");
        }

        void CheckEnd(int e, VertexId vertex, double t, string which)
        {
            var edge = body.Edges[e];
            var gap = (edge.Curve.At(t) - body[vertex].Point).Length;
            if (gap > Math.Max(edge.Tolerance, body[vertex].Tolerance))
                Add(ProblemKind.EdgeEnds, $"edge {e}", $"its curve's {which} is {gap:g3} from vertex {vertex.Index}");
        }

        for (var l = 0; l < body.Loops.Length; l++)
        {
            var loopId = new LoopId(l);
            if (Known(body[loopId].Face) || KnownLoop(loopId)) continue;
            var surface = body[body[loopId].Face].Surface;
            foreach (var c in body.CoedgesOf(loopId))
            {
                var coedge = body[c];
                var edge = body[coedge.Edge];
                var next = coedge.Next;
                var where = $"face {body[loopId].Face.Index} loop {l} coedge {c.Index}";
                if (body.EndOf(c) != body.StartOf(next))
                    Add(ProblemKind.LoopBroken, where, $"ends at vertex {body.EndOf(c).Index}, the next coedge starts at {body.StartOf(next).Index}");

                var worst = 0.0;
                for (var i = 0; i < Samples; i++)
                {
                    var t = edge.T0 + (edge.T1 - edge.T0) * i / (Samples - 1);
                    var uv = coedge.Pcurve.At(t);
                    worst = Math.Max(worst, (surface.At(uv.X, uv.Y) - edge.Curve.At(t)).Length);
                }
                if (worst > edge.Tolerance)
                    Add(ProblemKind.PcurveOffEdge, where, $"its p-curve is up to {worst:g3} off edge {coedge.Edge.Index}");

                // The jump from this p-curve's end to the next one's start, measured in space through the surface's
                // first derivatives: a seam taken on the wrong side jumps a whole period.
                var end = coedge.Pcurve.At(body.ParametersOf(c).To);
                var start = body[next].Pcurve.At(body.ParametersOf(next).From);
                var jump = surface.DerivativeU(end.X, end.Y) * (start.X - end.X) + surface.DerivativeV(end.X, end.Y) * (start.Y - end.Y);
                if (jump.Length > body[body.EndOf(c)].Tolerance)
                    Add(ProblemKind.LoopOpenInUv, where, $"its p-curve ends at ({end.X:g6}, {end.Y:g6}), the next starts at ({start.X:g6}, {start.Y:g6})");
            }
        }

        for (var f = 0; f < body.Faces.Length; f++)
        {
            if (Known(new FaceId(f))) continue;
            var face = body.Faces[f];
            var sense = face.SameSense ? 1 : -1;
            for (var i = 0; i < face.Loops.Length; i++)
            {
                if (KnownLoop(face.Loops[i])) continue;
                var area = sense * MassProperties.SignedUvArea(body, face.Loops[i]);
                if (i == 0 ? area <= 0 : area >= 0)
                    Add(ProblemKind.FaceOrientation, $"face {f} loop {face.Loops[i].Index}",
                        $"{(i == 0 ? "the outer loop" : "a hole")} runs {(area > 0 ? "counter-clockwise" : "clockwise")} seen from outside");
                if (i > 0 && !Inside(body, face.Loops[i], face.Loops[0]))
                    Add(ProblemKind.HoleOutside, $"face {f} loop {face.Loops[i].Index}", "the hole is not inside the outer loop");
            }
        }

        for (var s = 0; s < body.Shells.Length; s++)
        {
            var shell = new ShellId(s);
            var faces = body[shell].Faces;
            if (faces.IsEmpty) continue;
            if (!Connected(body, shell)) Add(ProblemKind.ShellDisconnected, $"shell {s}", "its faces are not one connected piece");

            var twiceEuler = TwiceEuler(body, shell); // V − E + F − (L − F) = 2(1 − G)
            if (twiceEuler % 2 != 0 || twiceEuler > 2)
                Add(ProblemKind.ShellEuler, $"shell {s}", $"V − E + 2F − L = {twiceEuler} fits no genus");

            if (faces.Any(Known)) continue;
            var volume = MassProperties.Volume(body, shell);
            if (s == 0 ? volume <= 0 : volume >= 0)
                Add(ProblemKind.ShellOrientation, $"shell {s}", $"{(s == 0 ? "the outer shell" : "a void")} encloses {volume:g6}");
        }
        return problems;
    }

    /// <summary>The genus of a closed shell from the Euler–Poincaré formula (0 for a block, 1 for a plate with a hole).</summary>
    public static int Genus(Body body, ShellId shell) => 1 - TwiceEuler(body, shell) / 2;

    // V − E + 2F − L, a pole's edge (where a loop runs along a surface's pole) not counted.
    static int TwiceEuler(Body body, ShellId shell)
    {
        var faces = body[shell].Faces;
        var coedges = faces.SelectMany(f => body[f].Loops).SelectMany(body.CoedgesOf).ToList();
        var v = coedges.Select(body.StartOf).Distinct().Count();
        var e = coedges.Select(c => body[c].Edge).Distinct().Count(edge => body[edge].Curve is not Pole);
        var l = faces.Sum(f => body[f].Loops.Length);
        return v - e + 2 * faces.Length - l;
    }

    static bool Connected(Body body, ShellId shell)
    {
        var faces = body[shell].Faces;
        var seen = new HashSet<FaceId> { faces[0] };
        var todo = new Stack<FaceId>([faces[0]]);
        while (todo.TryPop(out var f))
            foreach (var c in body[f].Loops.SelectMany(body.CoedgesOf))
            foreach (var use in body.UsesOf(body[c].Edge))
                if (seen.Add(body.FaceOf(use))) todo.Push(body.FaceOf(use));
        return faces.All(seen.Contains);
    }

    // Whether the hole's first point lies inside the outer loop in (u, v): winding number of the outer loop's p-curves,
    // taken as a fine polygon, with exact orientation tests. Faces that pass the other checks keep their holes well
    // away from the outer loop, where the polygon is exact enough.
    static bool Inside(Body body, LoopId hole, LoopId outer)
    {
        var first = body[hole].First;
        var p = body[first].Pcurve.At(body.ParametersOf(first).From);
        var polygon = new List<Point2D>();
        foreach (var c in body.CoedgesOf(outer))
        {
            var (from, to) = body.ParametersOf(c);
            var pieces = body[body[c].Edge].Curve.Period is null ? 1 : 64;
            for (var i = 0; i < pieces; i++) polygon.Add(body[c].Pcurve.At(from + (to - from) * i / pieces));
        }
        var winding = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            if (a.Y <= p.Y) { if (b.Y > p.Y && Predicates.Orient2D(a, b, p) > 0) winding++; }
            else if (b.Y <= p.Y && Predicates.Orient2D(a, b, p) < 0) winding--;
        }
        return winding != 0;
    }
}
