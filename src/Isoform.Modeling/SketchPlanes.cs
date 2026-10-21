using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;

namespace Isoform.Modeling;

/// <summary>Where sketches and face holes lie: the plane of a face of the part.</summary>
public static class SketchPlanes
{
    /// <summary>
    /// The plane of a flat face as a sketch plane: its outward normal as the part has it here, the world origin put onto
    /// it as the origin and the world x put onto it as the x direction (world y where x runs along the normal), so a
    /// sketch on a top face reads in world coordinates. The frame is right-handed with its normal out of the part, so on a
    /// face looking the other way (a bottom face) sketch y runs along world −y. Null when no flat surface has that name yet.
    /// </summary>
    public static SketchPlane? OfFace(string face, IReadOnlyList<Surface> surfaces, Field? tree)
    {
        if (tree is null || surfaces.LastOrDefault(s => s.Source == face) is not { Kind: SurfaceKind.Plane } found) return null;
        var n = TopologyBuilder.Oriented(surfaces, tree)[found.Id].Axis.Normalized();
        var o = n * n.Dot(found.Origin.AsVector());
        var x = new Vector3D(1, 0, 0) - n * n.X;
        if (x.Length < 0.1) x = new Vector3D(0, 1, 0) - n * n.Y;
        x = x.Normalized();
        return new SketchPlane([o.X, o.Y, o.Z], [x.X, x.Y, x.Z], [n.X, n.Y, n.Z]);
    }
}
