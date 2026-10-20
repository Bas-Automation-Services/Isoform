using System.Globalization;
using System.Text;
using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Step;

/// <summary>
/// Writes a <see cref="Brep"/> as a STEP AP214 file (ISO 10303-21): one product whose shape is an advanced B-rep,
/// one MANIFOLD_SOLID_BREP per solid, in millimetres. Numbers are written round-trip exact, so the file carries the
/// geometry to the last bit.
/// </summary>
public static class StepWriter
{
    public static void Write(Brep brep, string path, string name = "part") => File.WriteAllText(path, Write(brep, name));

    public static string Write(Brep brep, string name = "part")
    {
        var w = new Entities();
        var protocol = w.Add("APPLICATION_CONTEXT('core data for automotive mechanical design processes')");
        w.Add($"APPLICATION_PROTOCOL_DEFINITION('international standard','automotive_design',2000,{protocol})");
        var productContext = w.Add($"PRODUCT_CONTEXT('',{protocol},'mechanical')");
        var product = w.Add($"PRODUCT({S(name)},{S(name)},'',({productContext}))");
        w.Add($"PRODUCT_RELATED_PRODUCT_CATEGORY('part',$,({product}))");
        var formation = w.Add($"PRODUCT_DEFINITION_FORMATION('','',{product})");
        var definitionContext = w.Add($"PRODUCT_DEFINITION_CONTEXT('part definition',{protocol},'design')");
        var definition = w.Add($"PRODUCT_DEFINITION('design','',{formation},{definitionContext})");
        var shape = w.Add($"PRODUCT_DEFINITION_SHAPE('','',{definition})");

        var mm = w.Add("( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) )");
        var rad = w.Add("( NAMED_UNIT(*) PLANE_ANGLE_UNIT() SI_UNIT($,.RADIAN.) )");
        var sr = w.Add("( NAMED_UNIT(*) SI_UNIT($,.STERADIAN.) SOLID_ANGLE_UNIT() )");
        var uncertainty = w.Add($"UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE(1.E-07),{mm},'distance_accuracy_value','confusion accuracy')");
        var context = w.Add($"( GEOMETRIC_REPRESENTATION_CONTEXT(3) GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT(({uncertainty})) " +
                            $"GLOBAL_UNIT_ASSIGNED_CONTEXT(({mm},{rad},{sr})) REPRESENTATION_CONTEXT('Context #1','3D Context with UNIT and UNCERTAINTY') )");

        var vertices = brep.Vertices.Select(v => w.Add($"VERTEX_POINT('',{w.Point(v.Point)})")).ToArray();
        var edges = brep.Edges.Select(e => w.Add($"EDGE_CURVE('',{vertices[e.Start]},{vertices[e.End]},{CurveEntity(w, e.Curve)},.T.)")).ToArray();

        var solids = new List<string>();
        foreach (var solid in brep.Solids)
        {
            var faces = solid.Faces.Select(face =>
            {
                var bounds = face.Loops.Select(loop =>
                {
                    var coedges = loop.Coedges.Select(c => w.Add($"ORIENTED_EDGE('',*,*,{edges[c.Edge]},{B(c.Forward)})"));
                    var edgeLoop = w.Add($"EDGE_LOOP('',({string.Join(",", coedges)}))");
                    return w.Add($"{(loop.Outer ? "FACE_OUTER_BOUND" : "FACE_BOUND")}('',{edgeLoop},.T.)");
                }).ToList();
                return w.Add($"ADVANCED_FACE({S(string.Join("+", face.Sources))},({string.Join(",", bounds)}),{SurfaceEntity(w, face.Surface)},{B(face.SameSense)})");
            }).ToList();
            var shell = w.Add($"CLOSED_SHELL('',({string.Join(",", faces)}))");
            solids.Add(w.Add($"MANIFOLD_SOLID_BREP({S(solid.Name)},{shell})"));
        }

        var representation = w.Add($"ADVANCED_BREP_SHAPE_REPRESENTATION({S(name)},({string.Join(",", solids)}),{context})");
        w.Add($"SHAPE_DEFINITION_REPRESENTATION({shape},{representation})");

        var sb = new StringBuilder();
        sb.Append("ISO-10303-21;\nHEADER;\n");
        sb.Append("FILE_DESCRIPTION(('Isoform exact B-rep'),'2;1');\n");
        sb.Append($"FILE_NAME({S(name)},'',(''),(''),'Isoform','Isoform','');\n");
        sb.Append("FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));\nENDSEC;\nDATA;\n");
        for (var i = 0; i < w.Lines.Count; i++) sb.Append($"#{i + 1}={w.Lines[i]};\n");
        sb.Append("ENDSEC;\nEND-ISO-10303-21;\n");
        return sb.ToString();
    }

    static string CurveEntity(Entities w, Curve curve) => curve switch
    {
        Line l => w.Add($"LINE('',{w.Point(l.Origin)},{w.Add($"VECTOR('',{w.Direction(l.Direction)},1.)")})"),
        Circle c => w.Add($"CIRCLE('',{w.Placement(c.Center, c.Normal, c.X)},{R(c.Radius)})"),
        Ellipse e => w.Add($"ELLIPSE('',{w.Placement(e.Center, e.Normal, e.X)},{R(e.Major)},{R(e.Minor)})"),
        _ => throw new NotSupportedException(curve.GetType().Name),
    };

    // STEP surface normals follow the placement's z axis outward, as Isoform's distance gradients do.
    static string SurfaceEntity(Entities w, Surface s)
    {
        var place = w.Placement(s.Origin, s.Axis, SurfaceIntersection.Perpendicular(s.Axis));
        return s.Kind switch
        {
            SurfaceKind.Plane => w.Add($"PLANE('',{place})"),
            SurfaceKind.Cylinder => w.Add($"CYLINDRICAL_SURFACE('',{place},{R(s.Radius)})"),
            SurfaceKind.Cone => w.Add($"CONICAL_SURFACE('',{place},0.,{R(s.Angle)})"),
            SurfaceKind.Sphere => w.Add($"SPHERICAL_SURFACE('',{place},{R(s.Radius)})"),
            SurfaceKind.Torus => w.Add($"TOROIDAL_SURFACE('',{place},{R(s.Radius)},{R(s.Minor)})"),
            _ => throw new NotSupportedException($"{s.Kind} surface"),
        };
    }

    sealed class Entities
    {
        public List<string> Lines { get; } = [];

        public string Add(string entity)
        {
            Lines.Add(entity);
            return $"#{Lines.Count}";
        }

        public string Point(Point3D p) => Add($"CARTESIAN_POINT('',({R(p.X)},{R(p.Y)},{R(p.Z)}))");

        public string Direction(Vector3D v)
        {
            var u = v.Normalized();
            return Add($"DIRECTION('',({R(u.X)},{R(u.Y)},{R(u.Z)}))");
        }

        public string Placement(Point3D origin, Vector3D z, Vector3D x) => Add($"AXIS2_PLACEMENT_3D('',{Point(origin)},{Direction(z)},{Direction(x)})");
    }

    /// <summary>A STEP real: round-trip digits, always with a decimal point.</summary>
    static string R(double d)
    {
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        if (s.Contains('.')) return s;
        var e = s.IndexOf('E');
        return e < 0 ? s + "." : s.Insert(e, ".");
    }

    static string B(bool b) => b ? ".T." : ".F.";

    static string S(string text) => $"'{text.Replace("'", "''")}'";
}
