using Isoform.Meshing;

namespace Isoform.Modeling;

/// <summary>
/// Regeneration results kept by document hash and settings, so a part that did not change costs nothing: an assembly's
/// untouched parts, a component moved, an undo. A part kept also keeps its regions in the region cache, so an edit to
/// it later still re-meshes only what the edit changed. Call <see cref="Sweep"/> once per regeneration: it drops what
/// neither of the last two used. Not for concurrent use.
/// </summary>
public sealed class PartCache
{
    sealed class Entry(FrepResult result, UInt128[] regions, int used)
    {
        public readonly FrepResult Result = result;
        public readonly UInt128[] Regions = regions;
        public int Used = used;
    }

    readonly Dictionary<(string Hash, ModelingSettings Settings), Entry> _entries = [];
    int _generation;

    public FrepResult? TryGet(string hash, ModelingSettings settings, RegionCache? regions)
    {
        if (!_entries.TryGetValue((hash, settings), out var entry)) return null;
        entry.Used = _generation;
        regions?.Touch(entry.Regions);
        return entry.Result;
    }

    /// <param name="regions">The keys of the regions the result was meshed from.</param>
    public void Add(string hash, ModelingSettings settings, FrepResult result, UInt128[] regions) =>
        _entries[(hash, settings)] = new Entry(result, regions, _generation);

    public void Sweep()
    {
        foreach (var (key, entry) in _entries)
            if (entry.Used < _generation - 1) _entries.Remove(key);
        _generation++;
    }
}
