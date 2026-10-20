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
