using Isoform.Document;

namespace Isoform.Sketching;

public static partial class SketchEdits
{
    /// <summary>
    /// Rounds the corner at point <paramref name="corner"/>, where two lines or arcs meet, with a tangent arc of
    /// <paramref name="radius"/>. The corner stays as a construction point on both, so dimensions to it still hold.
    /// </summary>
    public static SketchEdit Fillet(Sketch sketch, string corner, double radius) => throw new NotImplementedException();
}
