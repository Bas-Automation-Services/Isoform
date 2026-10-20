using static Isoform.Occt.Native;

namespace Isoform.Occt;

public enum OcctFaceKind { Plane, Cylinder, Torus, Other }

/// <summary>Plane: origin, outward normal. Cylinder: axis point, axis, radius. Torus: centre, axis, major, minor.</summary>
public sealed record OcctFace(OcctFaceKind Kind, double[] Data);

public enum OcctBoolean { Fuse, Cut, Common }

/// <summary>An OCCT shape; faces, edges and vertices are addressed by stable 0-based index.</summary>
public sealed class OcctShape
{
    internal ShapeHandle Handle { get; }

    internal OcctShape(ShapeHandle handle) => Handle = handle;

    public static OcctShape Box(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        Check(iso_make_box(x0, y0, z0, x1, y1, z1, out var h));
        return new(h);
    }

    public static OcctShape Cylinder(double px, double py, double pz, double dx, double dy, double dz, double r, double height)
    {
        Check(iso_make_cylinder(px, py, pz, dx, dy, dz, r, height, out var h));
        return new(h);
    }

    /// <summary>A large block with one face on the plane, extending along the normal.</summary>
    public static OcctShape HalfSpace(double[] point, double[] normal, double size)
    {
        Check(iso_make_halfspace(point[0], point[1], point[2], normal[0], normal[1], normal[2], size, out var h));
        return new(h);
    }

    public int FaceCount => Get((out int n) => iso_face_count(Handle, out n));
    public int EdgeCount => Get((out int n) => iso_edge_count(Handle, out n));
    public int VertexCount => Get((out int n) => iso_vertex_count(Handle, out n));
    public int SolidCount => Get((out int n) => iso_solid_count(Handle, out n));
    public bool IsValid => Get((out int n) => iso_is_valid(Handle, out n)) == 1;

    /// <summary>What OCCT's checker finds wrong with the shape, one problem per line; empty when it is valid.</summary>
    public string CheckReport
    {
        get
        {
            var buffer = new byte[1 << 16];
            Check(iso_check_report(Handle, buffer, buffer.Length));
            return System.Text.Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0));
        }
    }

    public double Volume
    {
        get
        {
            Check(iso_volume(Handle, out var v));
            return v;
        }
    }

    public OcctFace Face(int face)
    {
        var data = new double[8];
        Check(iso_face_info(Handle, face, out var kind, data));
        return new((OcctFaceKind)kind, data);
    }

    public double FaceDistance(int face, double x, double y, double z)
    {
        Check(iso_face_distance(Handle, face, x, y, z, out var d));
        return d;
    }

    public int[] EdgeFaces(int edge) => List((int[] buf, out int n) => iso_edge_faces(Handle, edge, buf, buf.Length, out n));

    public (int First, int Last) EdgeVertices(int edge)
    {
        Check(iso_edge_vertices(Handle, edge, out var a, out var b));
        return (a, b);
    }

    public int[] VertexFaces(int vertex) => List((int[] buf, out int n) => iso_vertex_faces(Handle, vertex, buf, buf.Length, out n));

    public void WriteStep(string path) => Check(iso_write_step(Handle, path));

    /// <summary>
    /// Reads a STEP file into one shape. Without <paramref name="heal"/> OCCT's shape healing is off, so the shape is
    /// what the file itself describes: a check that it needs no fixing.
    /// </summary>
    public static OcctShape ReadStep(string path, bool heal = true)
    {
        Check(heal ? iso_read_step(path, out var h) : iso_read_step_unhealed(path, out h));
        return new(h);
    }

    public double[] VertexPoint(int vertex)
    {
        var xyz = new double[3];
        Check(iso_vertex_point(Handle, vertex, xyz));
        return xyz;
    }

    /// <summary>Point at fraction <paramref name="s"/> of the edge's parameter range.</summary>
    public double[] EdgePoint(int edge, double s)
    {
        var xyz = new double[3];
        Check(iso_edge_point(Handle, edge, s, xyz));
        return xyz;
    }

    /// <summary>The largest edge or vertex tolerance in the shape.</summary>
    public double MaxTolerance
    {
        get
        {
            Check(iso_max_tolerance(Handle, out var t));
            return t;
        }
    }

    public double EdgeDistance(int edge, double x, double y, double z)
    {
        Check(iso_edge_distance(Handle, edge, x, y, z, out var d));
        return d;
    }

    /// <summary>Tessellates for display; returns the number of triangles.</summary>
    public int Tessellate(double deflection, double angle = 0.5)
    {
        Check(iso_tessellate(Handle, deflection, angle, out var n));
        return n;
    }

    public OcctOperation Boolean(OcctBoolean kind, OcctShape tool, bool simplify)
    {
        Check(iso_boolean((int)kind, Handle, tool.Handle, simplify ? 1 : 0, out var op));
        return new(op);
    }

    public OcctOperation Fillet(int[] edges, double radius)
    {
        Check(iso_fillet(Handle, edges, edges.Length, radius, out var op));
        return new(op);
    }

    internal delegate int CountCall(out int count);
    internal delegate int ListCall(int[] buffer, out int count);

    static int Get(CountCall call)
    {
        Check(call(out var n));
        return n;
    }

    internal static int[] List(ListCall call)
    {
        var buffer = new int[64];
        Check(call(buffer, out var n));
        return buffer[..n];
    }
}

/// <summary>One part occurrence of an assembly read from STEP: its component path, its part, and where it is.</summary>
/// <param name="Placement">Row-major 3×4 matrix [R | t] from the part's coordinates to the assembly's.</param>
/// <param name="Part">Index of the distinct part (product) it uses.</param>
public sealed record OcctOccurrence(string Path, string Product, double[] Placement, int Part, OcctShape Shape);

/// <summary>A STEP file read with its product structure by OCCT's XCAF reader.</summary>
public sealed record OcctAssembly(string Name, IReadOnlyList<OcctOccurrence> Occurrences)
{
    public static OcctAssembly ReadStep(string path)
    {
        Check(iso_read_step_assembly(path, out var h));
        using (h)
        {
            Check(iso_assembly_count(h, out var n));
            var occurrences = new List<OcctOccurrence>();
            for (var i = 0; i < n; i++)
            {
                var (name, product) = Names(h, i);
                var m = new double[12];
                Check(iso_assembly_placement(h, i, m, out var part));
                Check(iso_assembly_shape(h, i, out var shape));
                occurrences.Add(new(name, product, m, part, new OcctShape(shape)));
            }
            return new(Names(h, -1).Path, occurrences);
        }
    }

    static (string Path, string Product) Names(AssemblyHandle h, int i)
    {
        var (path, product) = (new byte[1024], new byte[1024]);
        Check(iso_assembly_names(h, i, path, product, path.Length));
        static string Text(byte[] b) => System.Text.Encoding.UTF8.GetString(b, 0, Array.IndexOf(b, (byte)0));
        return (Text(path), Text(product));
    }
}

/// <summary>A boolean or fillet with its OCCT history: where each argument face ended up.</summary>
public sealed class OcctOperation
{
    readonly OpHandle _handle;

    internal OcctOperation(OpHandle handle) => _handle = handle;

    public OcctShape Result()
    {
        Check(iso_op_result(_handle, out var h));
        return new(h);
    }

    /// <summary>Result faces that face <paramref name="face"/> of argument <paramref name="argument"/> became.</summary>
    public int[] Image(int argument, int face) =>
        OcctShape.List((int[] buf, out int n) => iso_op_image(_handle, argument, face, buf, buf.Length, out n));

    /// <summary>Result faces generated from edges or vertices of an argument, e.g. fillet faces.</summary>
    public int[] Generated(int argument) =>
        OcctShape.List((int[] buf, out int n) => iso_op_generated(_handle, argument, buf, buf.Length, out n));
}
