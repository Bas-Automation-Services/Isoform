using Isoform.Document;

namespace Isoform.Modeling;

/// <summary>Regenerates a document on the implicit (F-rep/CSG) core.</summary>
public sealed class FrepModeler(ModelingSettings settings) : IModeler
{
    public ModelingSettings Settings { get; } = settings;

    public ModelResult Regenerate(PartDocument document) => throw new NotImplementedException();
}
