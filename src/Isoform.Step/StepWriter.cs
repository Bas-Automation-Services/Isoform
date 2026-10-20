using System.Globalization;
using System.Text;
using BasAutomation.Geometry;
using Isoform.Geometry;
using Isoform.Modeling;

namespace Isoform.Step;

/// <summary>
/// Writes STEP AP214 files (ISO 10303-21) in millimetres: a part as one product whose shape is an advanced B-rep
/// (one MANIFOLD_SOLID_BREP per solid, BREP_WITH_VOIDS for a solid with cavities), or an assembly as one product per
/// distinct part placed by its occurrences. Edges on curved faces carry their p-curves. Numbers are written
/// round-trip exact, so the file carries the geometry to the last bit.
/// </summary>
public static class StepWriter
{
    public static void Write(Brep brep, string path, string name = "part") => File.WriteAllText(path, Write(brep, name));

    public static string Write(Brep brep, string name = "part")
    {
        var w = new Entities();
        Part(w, brep, name);
        return w.File(name);
    }

    /// <summary>
    /// An assembly: every distinct part (by content hash) once as a product named after its document, and every
    /// instance a placed occurrence of it named by its id (sub-assemblies flattened into their paths).
    /// </summary>
    public static string Write(AssemblyResult assembly, ModelingSettings settings, string name = "assembly")
    {
        var parts = new List<(string Name, Brep Brep)>();
        var index = new Dictionary<string, int>();
        var occurrences = new List<(string Id, int Part, Frame Placement)>();
        foreach (var instance in assembly.Instances)
        {
            if (!index.TryGetValue(instance.Hash, out var k))
            {
                if (instance.Result.Topology is null) throw new NotSupportedException($"{instance.Id}: the part has no geometry");
                index[instance.Hash] = k = parts.Count;
                parts.Add((Path.GetFileNameWithoutExtension(instance.Part), ExactBrep.Build(instance.Result, settings)));
            }
            occurrences.Add((instance.Id, k, instance.Placement));
        }
        return Write(parts, occurrences, name);
    }

    /// <summary>
    /// Parts placed by occurrences, as STEP product structure: each occurrence a NEXT_ASSEMBLY_USAGE_OCCURRENCE of its
    /// part in the assembly with a CONTEXT_DEPENDENT_SHAPE_REPRESENTATION carrying its placement.
    /// </summary>
    public static string Write(IReadOnlyList<(string Name, Brep Brep)> parts, IReadOnlyList<(string Id, int Part, Frame Placement)> occurrences, string name = "assembly")
    {
        var w = new Entities();
        var placed = parts.Select(p => Part(w, p.Brep, p.Name)).ToList();
        var top = w.Product(name);
        var placements = occurrences.Select(o =>
        {
            var f = o.Placement;
            if (f.X.Cross(f.Y).Dot(f.Z) < 0) throw new NotSupportedException($"{o.Id}: a mirrored placement");
            return w.Placement(f.Origin, f.Z, f.X);
        }).ToList();
        var representation = w.Add($"SHAPE_REPRESENTATION({S(name)},({string.Join(",", placements.Prepend(w.Origin))}),{w.Context})");
        w.Add($"SHAPE_DEFINITION_REPRESENTATION({top.Shape},{representation})");
        for (var i = 0; i < occurrences.Count; i++)
        {
            var (id, part, _) = occurrences[i];
            var usage = w.Add($"NEXT_ASSEMBLY_USAGE_OCCURRENCE({S(id)},{S(id)},'',{top.Definition},{placed[part].Definition},$)");
            var shape = w.Add($"PRODUCT_DEFINITION_SHAPE('','',{usage})");
            var transform = w.Add($"ITEM_DEFINED_TRANSFORMATION('','',{placed[part].Origin},{placements[i]})");
            var relation = w.Add($"( REPRESENTATION_RELATIONSHIP('','',{placed[part].Representation},{representation}) " +
                                 $"REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION({transform}) SHAPE_REPRESENTATION_RELATIONSHIP() )");
            w.Add($"CONTEXT_DEPENDENT_SHAPE_REPRESENTATION({relation},{shape})");
        }
        return w.File(name);
    }

    /// <summary>A product with its B-rep; its definition, representation and the origin placement an assembly places.</summary>
    static (string Definition, string Representation, string Origin) Part(Entities w, Brep brep, string name)
    {
        var product = w.Product(name);
        var vertices = brep.Vertices.Select(v => w.Add($"VERTEX_POINT('',{w.Point(v.Point)})")).ToArray();

        // Every face's surface first: the p-curves of its edges refer to it.
        var faces = brep.Solids.SelectMany(s => s.Faces).ToList();
        var surfaces = faces.ToDictionary(f => f, f => SurfaceEntity(w, f.Surface));
        var uses = new Dictionary<int, List<(BrepFace Face, UvCurve Uv)>>();
        foreach (var face in faces)
        foreach (var c in face.Loops.SelectMany(l => l.Coedges))
            if (c.Uv is { } uv)
            {
                if (!uses.TryGetValue(c.Edge, out var list)) uses[c.Edge] = list = [];
                list.Add((face, uv));
            }
        var edges = brep.Edges.Select(e =>
        {
            var curve = CurveEntity(w, e.Curve);
            if (uses.TryGetValue(e.Index, out var list))
            {
                var pcurves = string.Join(",", list.Select(u => w.Add($"PCURVE('',{surfaces[u.Face]},{w.Add($"DEFINITIONAL_REPRESENTATION('',({UvEntity(w, u.Uv)}),{w.Context2D})")})")));
                // A seam lies on one face twice, once at either side of its parameter range.
                var seam = list.Count == 2 && list[0].Face == list[1].Face;
                curve = w.Add($"{(seam ? "SEAM_CURVE" : "SURFACE_CURVE")}('',{curve},({pcurves}),.CURVE_3D.)");
            }
            return w.Add($"EDGE_CURVE('',{vertices[e.Start]},{vertices[e.End]},{curve},.T.)");
        }).ToArray();

        // A void's closed shell bounds the cavity seen from inside it: every face reversed, and the shell turned
        // round again by the .F. of its ORIENTED_CLOSED_SHELL.
        string Face(BrepFace face, bool reversed)
        {
            var bounds = face.Loops.Select(loop =>
            {
                var coedges = Written(loop, reversed);
                var edgeLoop = w.Add($"EDGE_LOOP('',({string.Join(",", coedges.Select(c => w.Add($"ORIENTED_EDGE('',*,*,{edges[c.Edge]},{B(c.Forward)})")))}))");
                return w.Add($"{(loop.Outer ? "FACE_OUTER_BOUND" : "FACE_BOUND")}('',{edgeLoop},.T.)");
            }).ToList();
            return w.Add($"ADVANCED_FACE({S(string.Join("+", face.Sources))},({string.Join(",", bounds)}),{surfaces[face]},{B(face.SameSense != reversed)})");
        }
        string Shell(BrepShell shell, bool reversed) => w.Add($"CLOSED_SHELL('',({string.Join(",", shell.Faces.Select(f => Face(f, reversed)))}))");

        var solids = brep.Solids.Select(solid =>
        {
            var outer = Shell(solid.Outer, false);
            if (solid.Voids.Count == 0) return w.Add($"MANIFOLD_SOLID_BREP({S(solid.Name)},{outer})");
            var voids = solid.Voids.Select(v => w.Add($"ORIENTED_CLOSED_SHELL('',*,{Shell(v, true)},.F.)"));
            return w.Add($"BREP_WITH_VOIDS({S(solid.Name)},{outer},({string.Join(",", voids)}))");
        }).ToList();

        var representation = w.Add($"ADVANCED_BREP_SHAPE_REPRESENTATION({S(name)},({string.Join(",", solids.Append(w.Origin))}),{w.Context})");
        w.Add($"SHAPE_DEFINITION_REPRESENTATION({product.Shape},{representation})");
        return (product.Definition, representation, w.Origin);
    }

    /// <summary>A loop as written: as it is, or turned round for a face of a void.</summary>
    static IEnumerable<BrepCoedge> Written(BrepLoop loop, bool reversed) =>
        reversed ? loop.Coedges.Reverse().Select(c => c with { Forward = !c.Forward }) : loop.Coedges;

    static string CurveEntity(Entities w, Curve curve) => curve switch
    {
        Line l => w.Add($"LINE('',{w.Point(l.Origin)},{w.Add($"VECTOR('',{w.Direction(l.Direction)},1.)")})"),
        Circle c => w.Add($"CIRCLE('',{w.Placement(c.Center, c.Normal, c.X)},{R(c.Radius)})"),
        Ellipse e => w.Add($"ELLIPSE('',{w.Placement(e.Center, e.Normal, e.X)},{R(e.Major)},{R(e.Minor)})"),
        _ => throw new NotSupportedException(curve.GetType().Name),
    };

    static string UvEntity(Entities w, UvCurve c)
    {
        if (c.Degree == 1 && c.Poles.Count == 2)
        {
            // A straight image as a line through its point at t = 0, moving as fast as the edge's parameter.
            var (p, q) = (c.Poles[0], c.Poles[1]);
            var (t0, t1) = (c.Knots[0], c.Knots[1]);
            var (du, dv) = ((q.U - p.U) / (t1 - t0), (q.V - p.V) / (t1 - t0));
            var speed = Math.Sqrt(du * du + dv * dv);
            var origin = w.Add($"CARTESIAN_POINT('',({R(p.U - du * t0)},{R(p.V - dv * t0)}))");
            var direction = w.Add($"DIRECTION('',({R(du / speed)},{R(dv / speed)}))");
            return w.Add($"LINE('',{origin},{w.Add($"VECTOR('',{direction},{R(speed)})")})");
        }
        var poles = string.Join(",", c.Poles.Select(p => w.Add($"CARTESIAN_POINT('',({R(p.U)},{R(p.V)}))")));
        return w.Add($"B_SPLINE_CURVE_WITH_KNOTS('',{c.Degree},({poles}),.UNSPECIFIED.,.F.,.F.,({string.Join(",", c.Multiplicities)}),({string.Join(",", c.Knots.Select(R))}),.UNSPECIFIED.)");
    }

    // STEP surface normals follow the placement's z axis outward, as Isoform's distance gradients do.
    static string SurfaceEntity(Entities w, Surface s)
    {
        var (x, _, z) = SurfaceParameters.Axes(s);
        var place = w.Placement(s.Origin, z, x);
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

        readonly string _protocol;
        readonly string _productContext;
        readonly string _definitionContext;

        public Entities()
        {
            _protocol = Add("APPLICATION_CONTEXT('core data for automotive mechanical design processes')");
            Add($"APPLICATION_PROTOCOL_DEFINITION('international standard','automotive_design',2000,{_protocol})");
            _productContext = Add($"PRODUCT_CONTEXT('',{_protocol},'mechanical')");
            _definitionContext = Add($"PRODUCT_DEFINITION_CONTEXT('part definition',{_protocol},'design')");
            var mm = Add("( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) )");
            var rad = Add("( NAMED_UNIT(*) PLANE_ANGLE_UNIT() SI_UNIT($,.RADIAN.) )");
            var sr = Add("( NAMED_UNIT(*) SI_UNIT($,.STERADIAN.) SOLID_ANGLE_UNIT() )");
            var uncertainty = Add($"UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE(1.E-07),{mm},'distance_accuracy_value','confusion accuracy')");
            Context = Add($"( GEOMETRIC_REPRESENTATION_CONTEXT(3) GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT(({uncertainty})) " +
                          $"GLOBAL_UNIT_ASSIGNED_CONTEXT(({mm},{rad},{sr})) REPRESENTATION_CONTEXT('Context #1','3D Context with UNIT and UNCERTAINTY') )");
            Context2D = Add("( GEOMETRIC_REPRESENTATION_CONTEXT(2) PARAMETRIC_REPRESENTATION_CONTEXT() REPRESENTATION_CONTEXT('2D SPACE','') )");
            Origin = Placement(new Point3D(0, 0, 0), new Vector3D(0, 0, 1), new Vector3D(1, 0, 0));
        }

        /// <summary>The 3D context: millimetres, radians, 1e-7 mm uncertainty.</summary>
        public string Context { get; }

        /// <summary>The context of p-curves, in their surface's parameters.</summary>
        public string Context2D { get; }

        /// <summary>The world placement at the origin, shared by every representation.</summary>
        public string Origin { get; }

        public string Add(string entity)
        {
            Lines.Add(entity);
            return $"#{Lines.Count}";
        }

        public (string Definition, string Shape) Product(string name)
        {
            var product = Add($"PRODUCT({S(name)},{S(name)},'',({_productContext}))");
            Add($"PRODUCT_RELATED_PRODUCT_CATEGORY('part',$,({product}))");
            var formation = Add($"PRODUCT_DEFINITION_FORMATION('','',{product})");
            var definition = Add($"PRODUCT_DEFINITION('design','',{formation},{_definitionContext})");
            return (definition, Add($"PRODUCT_DEFINITION_SHAPE('','',{definition})"));
        }

        public string Point(Point3D p) => Add($"CARTESIAN_POINT('',({R(p.X)},{R(p.Y)},{R(p.Z)}))");

        public string Direction(Vector3D v)
        {
            var u = v.Normalized();
            return Add($"DIRECTION('',({R(u.X)},{R(u.Y)},{R(u.Z)}))");
        }

        public string Placement(Point3D origin, Vector3D z, Vector3D x) => Add($"AXIS2_PLACEMENT_3D('',{Point(origin)},{Direction(z)},{Direction(x)})");

        public string File(string name)
        {
            var sb = new StringBuilder();
            sb.Append("ISO-10303-21;\nHEADER;\n");
            sb.Append("FILE_DESCRIPTION(('Isoform exact B-rep'),'2;1');\n");
            sb.Append($"FILE_NAME({S(name)},'',(''),(''),'Isoform','Isoform','');\n");
            sb.Append("FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));\nENDSEC;\nDATA;\n");
            for (var i = 0; i < Lines.Count; i++) sb.Append($"#{i + 1}={Lines[i]};\n");
            sb.Append("ENDSEC;\nEND-ISO-10303-21;\n");
            return sb.ToString();
        }
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
