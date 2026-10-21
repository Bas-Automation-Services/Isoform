using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;

namespace Isoform.Interactive;

public static partial class Handles
{
    sealed partial class Builder
    {
        /// <summary>
        /// A lattice: <c>cell</c> and <c>thickness</c> arrows on its face, and <c>skin</c> as a shell's thickness. The lattice
        /// sits on the world grid, so a new cell scales it about the origin: a strut's surface point p over the foot q on
        /// its line moves by (s − 1) q, an arrow along q with per a/|q|; a thicker strut moves p along its normal by half
        /// as much (per 2). Both are exact where the strut alone bounds the part, which the anchor is chosen for. A sheet
        /// is a level set whose level moves with the cell and the thickness, so its arrows move it to first order only:
        /// they name no face (per 2|∇f| along the normal for the thickness, as for struts about the origin for the cell).
        /// </summary>
        void Lattice(Lattice l)
        {
            if (Exact(l.Face) is not { } face || _quads[face.Id].Count == 0) return;
            var lattice = new LatticeField(DocumentRules.LatticeKindOf(l.Kind)!.Value, l.Cell, l.Thickness, face);
            var field = result.Part.Field!;
            var points = _quads[face.Id].SelectMany(q => Corners(_mesh.Quads[q])).Distinct().Select(v => _mesh.Vertices[v]).ToList();
            var middle = Middle(l.Face)!.Value.At;
            string[] faces = lattice.Sheets ? [] : [l.Face];

            // The part is the lattice alone along the arrow, from before the anchor to past where the drag takes it.
            bool Alone(Point3D p, Vector3D axis) =>
                new[] { -0.4, -0.2, 0, 0.3, 0.6, 0.9 }.All(t => Math.Abs(field.Value(p + axis * t) - lattice.Value(p + axis * t)) < 1e-9);

            (Point3D At, Vector3D Axis, double Per)? Anchor(IEnumerable<Point3D> candidates, bool cell)
            {
                foreach (var v in candidates.Take(400))
                {
                    if (lattice.Strut(v) is { } strut)
                    {
                        var (q, d, next) = strut;
                        // On the strut's circle round q: the point facing away from the arrow, so the arrow enters there.
                        var along = cell ? q.AsVector().Normalized() : (v - q).Normalized();
                        var across = along - d * along.Dot(d);
                        if (across.Length < 0.6 || next < l.Thickness / 2 + 1 || q.AsVector().Length < l.Cell) continue;
                        var p = cell ? q - across.Normalized() * (l.Thickness / 2) : q + along * (l.Thickness / 2);
                        if (Alone(p, along)) return (p, along, cell ? l.Cell / q.AsVector().Length : 2);
                        continue;
                    }
                    // A sheet: onto the surface along the field's gradient, which is at most 1 long.
                    var s = v;
                    for (var i = 0; i < 8; i++)
                    {
                        var g = lattice.Sample(s);
                        s -= g.Gradient * (g.Value / g.Gradient.LengthSquared);
                    }
                    var at = lattice.Sample(s);
                    var normal = at.Gradient.Normalized();
                    var axis = cell ? s.AsVector().Normalized() : normal;
                    if (Math.Abs(at.Value) > 1e-9 || s.AsVector().Length < l.Cell || Math.Abs(axis.Dot(normal)) < 0.6 || !Alone(s, axis)) continue;
                    return (s, axis, cell ? l.Cell / s.AsVector().Length : 2 * at.Gradient.Length);
                }
                return null;
            }

            if (Anchor(points.OrderBy(p => (p - middle).LengthSquared), cell: false) is { } thick)
                _handles.Add(Make(l, "thickness", HandleKind.Linear, $"{l.Id} thickness", thick.At, thick.Axis, faces, ("thickness", l.Thickness, [thick.Per]))
                    with { Min = MinSize, Step = 0.1 });
            if (Anchor(points.OrderByDescending(p => p.AsVector().LengthSquared), cell: true) is { } scale)
                _handles.Add(Make(l, "cell", HandleKind.Linear, $"{l.Id} cell", scale.At, scale.Axis, faces, ("cell", l.Cell, [scale.Per]))
                    with { Min = MinSize, Step = 0.5 });
            if (l.Skin > 0) Skin(l, "skin", l.Skin);
        }
    }
}
