using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;
using IsoSurface = Isoform.Geometry.Surface;

namespace Isoform.Brep.Modeling;

/// <summary>
/// A document on both kernels: the managed B-rep kernel builds it as far as it can, and the F-rep core carries on from
/// there (lattices, smooth blends), each body the kernel made a field of its exact faces, names and all. The result is
/// the F-rep core's, as the rest of Isoform takes it.
/// </summary>
public sealed class MixedModeler(ModelingSettings settings) : IModeler
{
    ModelResult IModeler.Regenerate(PartDocument document) => Regenerate(document);

    public FrepResult Regenerate(PartDocument document)
    {
        var (count, solids, bodyOf) = new BrepModeler(settings).Head(document);
        var names = document.Features.Where(f => bodyOf.ContainsKey(f.Id)).Select(f => bodyOf[f.Id]).Distinct()
            .Where(name => solids.Any(s => s.Body == name));
        List<HeadBody> bodies = [.. names.Select(name => new HeadBody(name, [.. bodyOf.Where(kv => kv.Value == name).Select(kv => kv.Key)], register =>
        {
            var field = new BrepField([.. solids.Where(s => s.Body == name).Select(s => s.Solid)], register);
            return (field, field.Min, field.Max);
        }))];
        return new FrepModeler(settings) { Head = count == 0 ? null : new PartHead(count, bodies) }.Regenerate(document);
    }
}

/// <summary>
/// The kernel's solids of one body as a field: the signed distance to the nearest face (exact), owned by its surface,
/// registered under the face's name (and, for a face merged on one surface, its others). Its leaves are the faces
/// with their material sides, for a shell.
/// </summary>
sealed class BrepField : FacesField
{
    readonly List<(BodyDistance Distance, int[] Owner)> solids = [];
    readonly Dictionary<int, LeafField> byId = [];
    readonly List<LeafField> leaves = [];

    public BrepField(IReadOnlyList<NamedBody> parts, Func<IsoSurface, IsoSurface> register)
    {
        foreach (var (body, names) in parts)
        {
            var owner = new int[body.Faces.Length];
            for (var f = 0; f < body.Faces.Length; f++)
            {
                var face = body.Faces[f];
                IReadOnlyList<string> sources = names.TryGetValue(new FaceId(f), out var n) && n.Count > 0 ? n : [""];
                var surface = StepConversion.Surface(face.Surface, 0, sources[0]) with { Also = [.. sources.Skip(1)] };
                var leaf = new LeafField(register(surface), complement: !face.SameSense);
                leaves.Add(leaf);
                byId[leaf.Surface.Id] = leaf;
                owner[f] = leaf.Surface.Id;
            }
            solids.Add((new BodyDistance(body), owner));
        }
        var boxes = solids.Select(s => s.Distance.Box).ToList();
        Min = new Point3D(boxes.Min(b => b.Min.X), boxes.Min(b => b.Min.Y), boxes.Min(b => b.Min.Z));
        Max = new Point3D(boxes.Max(b => b.Max.X), boxes.Max(b => b.Max.Y), boxes.Max(b => b.Max.Z));
    }

    public Point3D Min { get; }

    public Point3D Max { get; }

    public override IReadOnlyList<LeafField> Leaves => leaves;

    // The solids are apart: the nearest one's.
    public override FieldSample Sample(Point3D p)
    {
        FieldSample? best = null;
        foreach (var (distance, owner) in solids)
        {
            var (value, face, gradient) = distance.Sample(p);
            if (best is null || value < best.Value.Value) best = new FieldSample(value, owner[face.Index], gradient);
        }
        return best!.Value;
    }

    public override FieldSample? SampleSurface(int owner, Point3D p) => byId.TryGetValue(owner, out var leaf) ? leaf.Sample(p) : null;

    // A distance changes no faster than the point moves.
    public override Interval Bounds(Box3D box)
    {
        var (c, half) = (box.Center, (box.Max - box.Min).Length / 2);
        var v = Value(c);
        return new Interval(v - half, v + half);
    }
}
