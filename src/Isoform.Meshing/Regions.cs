using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Isoform.Geometry;

namespace Isoform.Meshing;

/// <summary>
/// One region of a <see cref="MeshPlan"/>: an octree node of region size, or a larger leaf. It owns the vertices of
/// its leaves and the polygons of the edges whose (+,+) quadrant leaf is one of them, so every vertex and polygon of
/// the mesh belongs to exactly one region.
/// </summary>
public sealed class MeshRegion
{
    internal MeshRegion(int index, DualContouring.Node node, Field near, UInt128 key, int[] surfaces)
    {
        (Index, Node, Near, Key, Surfaces) = (index, node, near, key, surfaces);
        Local = new Dictionary<int, int>(surfaces.Length);
        for (var k = 0; k < surfaces.Length; k++) Local[surfaces[k]] = k;
    }

    /// <summary>Its place in <see cref="MeshPlan.Regions"/>: depth-first order.</summary>
    public int Index { get; }

    internal DualContouring.Node Node { get; }

    /// <summary>The field simplified for the region grown by one cell: what its vertices on several surfaces use.</summary>
    internal Field Near { get; }

    /// <summary>
    /// Content key: everything the region's result depends on, hashed. Regions with equal keys mesh to the same
    /// result, in any plan, in any process; surfaces are numbered by first appearance, not by id.
    /// </summary>
    public UInt128 Key { get; }

    /// <summary>This plan's surface ids in the key's numbering: owners in a <see cref="RegionMesh"/> index this.</summary>
    internal int[] Surfaces { get; }

    internal Dictionary<int, int> Local { get; }
}

/// <summary>
/// What one region adds to the mesh: the vertices of its leaves, the polygons of its edges and its thin spots. Cells
/// and edges are on the absolute lattice and owners are local surface numbers, so the result fits any plan whose
/// region has the same key.
/// </summary>
public sealed class RegionMesh
{
    internal const int PolygonInts = 19; // edge start (3), axis, length, four leaf corners (12), owner, inside

    internal int[] Cells = [];        // per vertex: its leaf's corner
    internal double[] Positions = []; // per vertex: x, y, z
    internal int[] Polygons = [];
    internal int[] ThinOwners = [];   // per thin spot: two owners
    internal double[] Thin = [];      // per thin spot: x, y, z, thickness

    /// <summary>False when a vertex needed the whole field (a coarse node at the region's side): not reusable.</summary>
    public bool Cacheable { get; internal set; } = true;

    public int VertexCount => Cells.Length / 3;

    public void Write(BinaryWriter w)
    {
        static void Array<T>(BinaryWriter w, T[] a) where T : struct
        {
            w.Write(a.Length);
            w.Write(MemoryMarshal.AsBytes(a.AsSpan()));
        }
        Array(w, Cells);
        Array(w, Positions);
        Array(w, Polygons);
        Array(w, ThinOwners);
        Array(w, Thin);
    }

    public static RegionMesh Read(BinaryReader r)
    {
        static T[] Array<T>(BinaryReader r) where T : struct
        {
            var a = new T[r.ReadInt32()];
            r.Read(MemoryMarshal.AsBytes(a.AsSpan()));
            return a;
        }
        return new RegionMesh { Cells = Array<int>(r), Positions = Array<double>(r), Polygons = Array<int>(r), ThinOwners = Array<int>(r), Thin = Array<double>(r) };
    }
}

/// <summary>
/// The octree down to region level, and the regions below it. Each region can be meshed on its own, in any order,
/// thread or process; <see cref="Assemble"/> combines the results into exactly the mesh of
/// <see cref="DualContouring.Build"/> for the same arguments.
/// </summary>
public sealed class MeshPlan
{
    internal MeshPlan(Field field, DualContouring.Context context, DualContouring.Node root, IReadOnlyList<MeshRegion> regions) =>
        (Field, Context, Root, Regions) = (field, context, root, regions);

    internal Field Field { get; }
    internal DualContouring.Context Context { get; }
    internal DualContouring.Node Root { get; }

    /// <summary>Every region holding surface, or that may: a region proves itself empty only when meshed.</summary>
    public IReadOnlyList<MeshRegion> Regions { get; }

    /// <summary>
    /// The expensive part: the region's octree, crossings, vertices, polygons and thin spots. The region builds the
    /// sides of its neighbours it needs, unless <see cref="MeshRegions"/> built them.
    /// </summary>
    public RegionMesh Mesh(MeshRegion region) => DualContouring.MeshRegion(this, region);

    /// <summary>
    /// Results for the regions <paramref name="which"/> selects (all by default; the others stay null), from the cache
    /// where it has them. The rest are meshed in parallel, their octrees built first so neighbours share them, and
    /// added to the cache.
    /// </summary>
    public RegionMesh?[] MeshRegions(RegionCache? cache, Func<MeshRegion, bool>? which = null)
    {
        var results = new RegionMesh?[Regions.Count];
        var missing = new List<int>();
        for (var i = 0; i < results.Length; i++)
            if (which is null || which(Regions[i]))
                if (cache?.TryGet(Regions[i].Key) is { } hit) results[i] = hit;
                else missing.Add(i);
        Parallel.ForEach(missing, i => DualContouring.Expand(this, Regions[i]));
        Parallel.ForEach(missing, i =>
        {
            results[i] = Mesh(Regions[i]);
            cache?.Add(Regions[i].Key, results[i]!);
        });
        return results;
    }

    /// <summary>
    /// Which of <paramref name="count"/> processes meshes the region: an equal run of <see cref="Regions"/>, which are
    /// in depth-first (Morton) order, so each process gets one compact piece of the part and builds its neighbours'
    /// sides only along the piece's border. While an edit keeps the number of regions, every region stays with the
    /// process that cached it.
    /// </summary>
    public int Share(MeshRegion region, int count) => (int)((long)region.Index * count / Regions.Count);

    /// <summary>The mesh from one result per region, in <see cref="Regions"/> order.</summary>
    public Mesh Assemble(IReadOnlyList<RegionMesh> results) => DualContouring.Assemble(this, results);

    /// <summary>Meshes every region, through <paramref name="cache"/> if given, and assembles them.</summary>
    public Mesh Build(RegionCache? cache = null) => Assemble(MeshRegions(cache)!);
}

/// <summary>
/// Region results kept across regenerations by content key, so an edit re-meshes only the regions it changed.
/// Thread-safe. Call <see cref="Sweep"/> once per regeneration: it drops what neither of the last two used.
/// </summary>
public sealed class RegionCache
{
    sealed class Entry(RegionMesh mesh, int used)
    {
        public readonly RegionMesh Mesh = mesh;
        public int Used = used;
    }

    readonly ConcurrentDictionary<UInt128, Entry> _entries = new();
    readonly ConcurrentQueue<UInt128> _computed = new();
    int _generation, _hits, _misses;

    public int Count => _entries.Count;

    public RegionMesh? TryGet(UInt128 key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return null;
        entry.Used = _generation;
        Interlocked.Increment(ref _hits);
        return entry.Mesh;
    }

    /// <summary>A result just meshed; kept unless it is not <see cref="RegionMesh.Cacheable"/>.</summary>
    public void Add(UInt128 key, RegionMesh mesh)
    {
        Interlocked.Increment(ref _misses);
        if (mesh.Cacheable && _entries.TryAdd(key, new Entry(mesh, _generation))) _computed.Enqueue(key);
    }

    /// <summary>Ends a regeneration: returns how many regions it reused and meshed, and drops stale entries.</summary>
    public (int Hits, int Misses) Sweep()
    {
        foreach (var (key, entry) in _entries)
            if (entry.Used < _generation - 1) _entries.TryRemove(key, out _);
        _generation++;
        return TakeCounts();
    }

    /// <summary>How many regions were reused and meshed since the last call or sweep.</summary>
    public (int Hits, int Misses) TakeCounts() => (Interlocked.Exchange(ref _hits, 0), Interlocked.Exchange(ref _misses, 0));

    /// <summary>The entries meshed here since the last call, serialized for <see cref="Import"/> in another process.</summary>
    public byte[] TakeComputed()
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        var list = new List<(UInt128, RegionMesh)>();
        while (_computed.TryDequeue(out var key))
            if (_entries.TryGetValue(key, out var e)) list.Add((key, e.Mesh));
        w.Write(list.Count);
        foreach (var (key, mesh) in list)
        {
            w.Write((ulong)key);
            w.Write((ulong)(key >> 64));
            mesh.Write(w);
        }
        w.Flush();
        return stream.ToArray();
    }

    /// <summary>Adds what <see cref="TakeComputed"/> wrote; returns how many results it held.</summary>
    public int Import(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        var count = r.ReadInt32();
        for (var n = 0; n < count; n++)
        {
            var lower = r.ReadUInt64();
            _entries[new UInt128(r.ReadUInt64(), lower)] = new Entry(RegionMesh.Read(r), _generation);
        }
        return count;
    }
}
