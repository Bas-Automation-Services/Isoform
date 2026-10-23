using Isoform.Document;

namespace Isoform.Sketching;

public static partial class SketchEdits
{
    /// <summary>
    /// Deletes the piece of <paramref name="entity"/> between its intersections with the sketch's other entities that
    /// lies nearest (<paramref name="x"/>, <paramref name="y"/>), in sketch coordinates. A trimmed circle becomes an arc;
    /// the piece kept keeps the entity's id.
    /// </summary>
    public static SketchEdit Trim(Sketch sketch, string entity, double x, double y) => throw new NotImplementedException();
}
