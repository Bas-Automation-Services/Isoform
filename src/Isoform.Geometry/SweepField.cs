using BasAutomation.Geometry;

namespace Isoform.Geometry;

/// <summary>
/// One piece of a flat sweep path. A line runs from <see cref="Frame"/>'s origin along its Z for <see cref="Length"/>,
/// with the section in its X (left of travel) and Y (the path plane's normal). An arc turns <see cref="Angle"/> round the
/// axis Y through the origin (its centre), from the radial direction X; θ runs from X towards W = Y × X, the direction
/// of travel at the start, as in a revolve.
/// </summary>
public sealed record SweepPiece(string Id, Frame Frame, double Length, double Angle, double Radius, int Turn)
{
    public bool Arc => Turn != 0;

    /// <summary>
    /// A piece of the path in sketch frame <paramref name="sketch"/>, running along edge <paramref name="e"/>; an arc
    /// may turn up to a whole circle (<paramref name="sweep"/>, signed, overrides the edge's own).
    /// </summary>
    public static SweepPiece Of(string id, ProfileEdge2D e, Frame sketch, double? sweep = null)
    {
        var p = sketch.ToWorld(new Point3D(e.U0, e.V0, 0));
        var n = sketch.Z;
        if (!e.IsArc)
        {
            var q = sketch.ToWorld(new Point3D(e.U1, e.V1, 0));
            var t = (q - p).Normalized();
            return new(id, new Frame(p, n.Cross(t), n, t), (q - p).Length, 0, 0, 0);
        }
        var turn = sweep ?? e.Sweep;
        var c = sketch.ToWorld(new Point3D(e.Cu, e.Cv, 0));
        // A left turn goes round +n, a right turn round −n: either way a positive angle round Y.
        var (x, y) = ((p - c) / e.R, n * Math.Sign(turn));
        return new(id, new Frame(c, x, y, x.Cross(y)), 0, Math.Abs(turn), e.R, Math.Sign(turn));
    }

    /// <summary>
    /// A section point (a left of travel, b along the path plane's normal) in this piece's profile coordinates: the
    /// same for a line, (distance from the axis, height along it) for an arc, which mirrors the section.
    /// </summary>
    public (double U, double V) Section(double a, double b) => Arc ? (Radius - Turn * a, Turn * b) : (a, b);

    /// <summary>Where the piece starts and its direction of travel there.</summary>
    public (Point3D At, Vector3D Along) Start => Arc ? (Frame.Origin + Frame.X * Radius, -Frame.Z) : (Frame.Origin, Frame.Z);

    /// <summary>Where the piece ends and its direction of travel there.</summary>
    public (Point3D At, Vector3D Along) End => Arc ? (Frame.Origin + Radial(Angle) * Radius, Along(Angle)) : (Frame.Origin + Frame.Z * Length, Frame.Z);

    /// <summary>The half-space behind the plane across the piece at angle θ (an arc) or distance θ (a line): its origin and outward normal.</summary>
    public (Point3D Origin, Vector3D Normal) Across(double at) => Arc ? (Frame.Origin, Along(at)) : (Frame.Origin + Frame.Z * at, Frame.Z);

    Vector3D Radial(double angle) => Frame.X * Math.Cos(angle) - Frame.Z * Math.Sin(angle);

    Vector3D Along(double angle) => -Frame.X * Math.Sin(angle) - Frame.Z * Math.Cos(angle);
}

/// <summary>
/// A profile swept along a flat, tangent-continuous path of lines and arcs: the union of one exact piece per path
/// entity (the profile extruded along a line, revolved round an arc's axis), each cut to its stretch of the path by the
/// planes across the path at its two ends. Every term is 1-Lipschitz and the union never overestimates the distance.
/// </summary>
/// <remarks>
/// Two neighbouring pieces meet in the plane across the path at their joint, where each is cut off: both are 0 there,
/// and the union would be 0 inside the solid on a disc no surface bounds. A third term covers the joint: both sections
/// uncut, max(A, B, back, ahead), with the planes back and ahead across the two pieces within them (an arc's at most a
/// quarter turn from the joint). Behind the joint it lies inside the first piece and ahead of it inside the second, so
/// the union keeps its set; on the disc both sections are the same profile, so there it is the section's own distance.
/// </remarks>
public static class SweepField
{
    /// <summary>
    /// The sweep from its pieces and each piece's uncut section field (in its frame, see <see cref="SweepPiece"/>).
    /// <paramref name="plane"/> makes a plane leaf by name, origin and outward normal: <c>start</c> and <c>end</c> for an
    /// open path's ends, <c>p+q</c> for the joint of pieces p and q, <c>p+q:p</c> for a plane across p used at that joint.
    /// </summary>
    public static Field Of(IReadOnlyList<SweepPiece> pieces, IReadOnlyList<Field> sections, bool closed, Func<string, Point3D, Vector3D, LeafField> plane)
    {
        var n = pieces.Count;
        if (closed && n == 1) return sections[0];
        // The plane across the path at the end of piece k, outward along the path: piece k + 1 has it complemented.
        var joints = new LeafField?[n];
        for (var k = 0; k < (closed ? n : n - 1); k++)
        {
            var (at, along) = pieces[k].End;
            joints[k] = plane($"{pieces[k].Id}+{pieces[(k + 1) % n].Id}", at, along);
        }
        var (startCap, endCap) = closed ? (null, null) : (plane("start", pieces[0].Start.At, -pieces[0].Start.Along), plane("end", pieces[^1].End.At, pieces[^1].End.Along));
        LeafField Behind(int k) => k > 0 || closed ? new LeafField(joints[(k - 1 + n) % n]!.Surface, complement: true) : startCap!;
        LeafField Ahead(int k) => joints[k] ?? endCap!;

        var terms = new List<Field>();
        for (var k = 0; k < n; k++)
        {
            var (behind, ahead) = (Behind(k), Ahead(k));
            // An arc of more than half a turn is the union of its two half-spaces, not their intersection.
            terms.Add(new MaxField([sections[k], pieces[k].Angle > Math.PI ? new MinField([behind, ahead]) : new MaxField([behind, ahead])]));
        }
        for (var k = 0; k < (closed ? n : n - 1); k++)
        {
            var (p, q) = (pieces[k], pieces[(k + 1) % n]);
            var name = $"{p.Id}+{q.Id}";
            // Within each piece, at most a quarter turn back from the joint: then the plane, the joint's and the section
            // keep to the piece. A line, or an arc of at most a quarter turn, uses its other end.
            var back = !p.Arc || p.Angle <= Math.PI / 2 ? Behind(k) : Clip($"{name}:{p.Id}", p, p.Angle - Math.PI / 2, ahead: false);
            var ahead = !q.Arc || q.Angle <= Math.PI / 2 ? Ahead((k + 1) % n) : Clip($"{name}:{q.Id}", q, Math.PI / 2, ahead: true);
            terms.Add(new MaxField([sections[k], sections[(k + 1) % n], back, ahead]));
        }
        return new MinField(terms);

        LeafField Clip(string name, SweepPiece piece, double at, bool ahead)
        {
            var (o, normal) = piece.Across(at);
            return plane(name, o, ahead ? normal : -normal);
        }
    }
}
