using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;

namespace Isoform.Interactive;

public static partial class Handles
{
    sealed partial class Builder
    {
        /// <summary>
        /// A planar handle on every profile corner, in the sketch plane at height <paramref name="w"/> along its normal
        /// (an extrude's end cap). Corner k is where edge k ends; the last edge ends at the start, so that corner moves
        /// the start and the last edge's end together. It moves the faces of the two edges that meet there.
        /// </summary>
        void Vertices(Feature f, Profile profile, Frame frame, double w)
        {
            var n = profile.Edges.Count;
            for (var k = 0; k < n; k++)
            {
                var (edge, next) = (profile.Edges[k], profile.Edges[(k + 1) % n]);
                var fields = new List<(string, double, double[])>
                {
                    ($"profile.edges.{k}.to.0", edge.To[0], [1.0, 0]), ($"profile.edges.{k}.to.1", edge.To[1], [0, 1.0]),
                };
                if (k == n - 1) fields.AddRange([("profile.start.0", profile.Start[0], [1.0, 0]), ("profile.start.1", profile.Start[1], [0, 1.0])]);
                _handles.Add(Make(f, $"corner:{edge.Id}", HandleKind.Planar, $"{f.Id} corner {edge.Id}/{next.Id}", frame.ToWorld(new Point3D(edge.To[0], edge.To[1], w)),
                    frame.Z, [$"{f.Id}/{edge.Id}", $"{f.Id}/{next.Id}"], [.. fields]) with { U = frame.X, V = frame.Y });
            }
        }
    }
}
