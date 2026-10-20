using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Isoform.Occt;

public sealed class OcctException(string message) : Exception(message);

internal sealed class ShapeHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        Native.iso_shape_free(handle);
        return true;
    }
}

internal sealed class OpHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        Native.iso_op_free(handle);
        return true;
    }
}

/// <summary>Bindings to native/Isoform.Occt (isoform_occt.dll plus the OCCT DLLs next to it).</summary>
internal static partial class Native
{
    const string Lib = "isoform_occt";

    public static bool IsAvailable { get; } = NativeLibrary.TryLoad(Lib, typeof(Native).Assembly, null, out _);

    public static void Check(int rc)
    {
        if (rc != 0) throw new OcctException(Marshal.PtrToStringUTF8(iso_last_error()) ?? "native failure");
    }

    [LibraryImport(Lib)] public static partial nint iso_last_error();
    [LibraryImport(Lib)] public static partial int iso_make_box(double x0, double y0, double z0, double x1, double y1, double z1, out ShapeHandle shape);
    [LibraryImport(Lib)] public static partial int iso_make_cylinder(double px, double py, double pz, double dx, double dy, double dz, double r, double h, out ShapeHandle shape);
    [LibraryImport(Lib)] public static partial int iso_make_halfspace(double px, double py, double pz, double nx, double ny, double nz, double size, out ShapeHandle shape);
    [LibraryImport(Lib)] public static partial void iso_shape_free(nint shape);

    [LibraryImport(Lib)] public static partial int iso_face_count(ShapeHandle shape, out int count);
    [LibraryImport(Lib)] public static partial int iso_face_info(ShapeHandle shape, int face, out int kind, [Out] double[] data);
    [LibraryImport(Lib)] public static partial int iso_face_distance(ShapeHandle shape, int face, double x, double y, double z, out double distance);
    [LibraryImport(Lib)] public static partial int iso_edge_count(ShapeHandle shape, out int count);
    [LibraryImport(Lib)] public static partial int iso_edge_faces(ShapeHandle shape, int edge, [Out] int[] faces, int capacity, out int count);
    [LibraryImport(Lib)] public static partial int iso_edge_vertices(ShapeHandle shape, int edge, out int first, out int last);
    [LibraryImport(Lib)] public static partial int iso_vertex_count(ShapeHandle shape, out int count);
    [LibraryImport(Lib)] public static partial int iso_vertex_faces(ShapeHandle shape, int vertex, [Out] int[] faces, int capacity, out int count);
    [LibraryImport(Lib)] public static partial int iso_solid_count(ShapeHandle shape, out int count);
    [LibraryImport(Lib)] public static partial int iso_is_valid(ShapeHandle shape, out int valid);
    [LibraryImport(Lib)] public static partial int iso_volume(ShapeHandle shape, out double volume);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int iso_write_step(ShapeHandle shape, string path);

    [LibraryImport(Lib)] public static partial int iso_boolean(int kind, ShapeHandle a, ShapeHandle b, int simplify, out OpHandle op);
    [LibraryImport(Lib)] public static partial int iso_fillet(ShapeHandle shape, [In] int[] edges, int edgeCount, double radius, out OpHandle op);
    [LibraryImport(Lib)] public static partial int iso_op_result(OpHandle op, out ShapeHandle shape);
    [LibraryImport(Lib)] public static partial int iso_op_image(OpHandle op, int argument, int face, [Out] int[] faces, int capacity, out int count);
    [LibraryImport(Lib)] public static partial int iso_op_generated(OpHandle op, int argument, [Out] int[] faces, int capacity, out int count);
    [LibraryImport(Lib)] public static partial void iso_op_free(nint op);
}
