using System.Text.Json.Nodes;
using Isoform.Document;

namespace Isoform.Tests;

/// <summary>
/// K7: seeded random documents for the robustness runs. Deterministic by seed. Coordinates are drawn either at random
/// or snapped to a face already placed plus a nudge (0, 1e-9 … 1 mm), so coincident faces, zero-thickness contacts,
/// slivers, near-tangent holes and cuts through vertices come up often instead of almost never.
/// </summary>
public static class RobustnessDocuments
{
    /// <summary>Blocks joined and cut, holes, plane cuts and single-edge fillets: what both backends support.</summary>
    public static PartDocument Common(int seed) => new Generator(new Random(seed)).Common();

    /// <summary>Everything the OCCT baseline lacks: sketches, patterns, loops, chamfers, shells, offsets, blends, bodies.</summary>
    public static PartDocument FrepOnly(int seed) => new Generator(new Random(seed)).FrepOnly();

    /// <summary>One feature per line: a repro that reads in a test file and parses back with <see cref="Parse"/>.</summary>
    public static string Compact(PartDocument doc)
    {
        var features = CanonicalFormat.ToNode(doc)["features"]!.AsArray().Select(f => "  " + f!.ToJsonString());
        return "[\n" + string.Join(",\n", features) + "\n]";
    }

    public static PartDocument Parse(string features) => CanonicalFormat.FromNode(new JsonObject
    {
        ["format"] = PartDocument.FormatVersion,
        ["units"] = PartDocument.Units,
        ["features"] = JsonNode.Parse(features),
    });

    /// <summary>
    /// Shrinks a document while <paramref name="fails"/> keeps holding: drops features (never the first), then rounds
    /// every number to the coarsest of 10, 1, 0.5, 0.1 that keeps it failing. Greedy, bounded by <paramref name="budget"/>
    /// predicate calls. Documents that no longer parse or validate simply do not fail.
    /// </summary>
    public static PartDocument Minimize(PartDocument doc, Func<PartDocument, bool> fails, int budget = 150)
    {
        var calls = 0;
        bool Try(JsonNode root, out PartDocument next)
        {
            next = null!;
            if (calls++ >= budget) return false;
            try { next = CanonicalFormat.FromNode(root); }
            catch (Exception) { return false; }
            try { return fails(next); }
            catch (Exception) { return false; }
        }

        var current = CanonicalFormat.ToNode(doc);
        for (var changed = true; changed && calls < budget;)
        {
            changed = false;
            for (var i = current["features"]!.AsArray().Count - 1; i >= 1; i--)
            {
                var candidate = current.DeepClone();
                candidate["features"]!.AsArray().RemoveAt(i);
                if (Try(candidate, out var smaller)) { (current, doc, changed) = (candidate, smaller, true); }
            }
        }

        // Round numbers one at a time; a value that is already round stays.
        for (var n = 0; calls < budget; n++)
        {
            var numbers = Numbers(current).ToList();
            if (n >= numbers.Count) break;
            var value = (double)numbers[n].Node;
            foreach (var step in new[] { 10, 1, 0.5, 0.1 })
            {
                var rounded = Math.Round(value / step) * step;
                if (rounded == value || Math.Abs(rounded - value) > Math.Max(0.5 * step, 1e-12)) continue;
                var candidate = current.DeepClone();
                numbers = Numbers(candidate).ToList();
                numbers[n].Replace(rounded);
                if (Try(candidate, out var simpler)) { (current, doc) = (candidate, simpler); break; }
            }
        }
        return doc;
    }

    /// <summary>The document with every number rounded to a multiple of <paramref name="grid"/>.</summary>
    public static PartDocument Snap(PartDocument doc, double grid)
    {
        var root = CanonicalFormat.ToNode(doc);
        foreach (var (node, replace) in Numbers(root).ToList()) replace(Math.Round((double)node / grid) * grid);
        return CanonicalFormat.FromNode(root);
    }

    /// <summary>Every number in the features, with a way to replace it in place.</summary>
    static IEnumerable<(JsonNode Node, Action<double> Replace)> Numbers(JsonNode root)
    {
        IEnumerable<(JsonNode, Action<double>)> Walk(JsonNode node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var key in o.Select(kv => kv.Key).ToList())
                    {
                        if (o[key] is JsonValue v && v.TryGetValue<double>(out _)) yield return (v, x => o[key] = x);
                        else if (o[key] is { } child) foreach (var r in Walk(child)) yield return r;
                    }
                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                    {
                        var index = i;
                        if (a[i] is JsonValue v && v.TryGetValue<double>(out _)) yield return (v, x => a[index] = x);
                        else if (a[i] is { } child) foreach (var r in Walk(child)) yield return r;
                    }
                    break;
            }
        }
        return Walk(root["features"]!);
    }

    sealed class Generator(Random rng)
    {
        readonly List<Feature> _features = [];
        readonly List<double>[] _planes = [[], [], []]; // face coordinates per axis, to snap to
        readonly List<double[]> _corners = [];
        readonly List<(string, string)> _edges = []; // likely edges, for fillets and chamfers
        readonly List<string> _faces = [];
        double[] _min = null!, _max = null!;
        int _next;

        string Id(string kind) => $"{kind}{++_next}";
        double U(double lo, double hi) => lo + rng.NextDouble() * (hi - lo);
        bool P(double p) => rng.NextDouble() < p;
        T Of<T>(IReadOnlyList<T> items) => items[rng.Next(items.Count)];

        // Mostly exact contact, then the values around the tolerance (1e-7), then sizable offsets.
        static readonly double[] Nudges = [0, 0, 0, 1e-9, 1e-7, 1e-6, 1e-6, 1e-4, 0.01, 0.1, 0.25, 0.5, 1];

        double Nudge() => Of(Nudges) * (P(0.5) ? 1 : -1);

        double Round(double x) => P(0.6) ? Math.Round(x * 2) / 2 : Math.Round(x, 3);

        /// <summary>A coordinate along an axis: an existing face plus a nudge, or a random value round the part.</summary>
        double Coord(int axis) => _planes[axis].Count > 0 && P(0.5)
            ? Of(_planes[axis]) + Nudge()
            : Round(U(_min[axis] - 3, _max[axis] + 3));

        /// <summary>A length: usually ordinary, sometimes a sliver or one near the resolution limit (2 × 0.5 mm cells).</summary>
        double Length(double typical) => rng.Next(10) switch
        {
            0 => Of([1e-6, 1e-4, 0.01, 0.05]),
            1 => Of([0.2, 0.3, 0.5, 0.7, 0.99, 1.0, 1.01]),
            _ => Round(U(0.5, typical)),
        };

        void AddBox(string id, double[] lo, double[] hi, bool join)
        {
            for (var a = 0; a < 3; a++) { _planes[a].Add(lo[a]); _planes[a].Add(hi[a]); }
            foreach (var x in new[] { lo[0], hi[0] })
            foreach (var y in new[] { lo[1], hi[1] })
            foreach (var z in new[] { lo[2], hi[2] })
                _corners.Add([x, y, z]);
            string[] sides = ["x-", "x+", "y-", "y+", "z-", "z+"];
            foreach (var s in sides) _faces.Add($"{id}/{s}");
            // Edges of the box itself, and where it meets the base plate.
            for (var i = 0; i < 6; i++)
                for (var j = i + 1; j < 6; j++)
                    if (sides[i][0] != sides[j][0]) _edges.Add(($"{id}/{sides[i]}", $"{id}/{sides[j]}"));
            // Where it meets the base: a foot edge for a boss, a rim for a cut.
            if (id != _features[0].Id)
                foreach (var s in sides.Where(s => s[0] != 'z'))
                    _edges.Add(($"{_features[0].Id}/z+", $"{id}/{s}"));
            if (join)
                for (var a = 0; a < 3; a++) { _min[a] = Math.Min(_min[a], lo[a]); _max[a] = Math.Max(_max[a], hi[a]); }
        }

        Block Plate()
        {
            double[] size = [Round(U(10, 60)), Round(U(10, 40)), Round(U(2, 15))];
            // Grid alignment matters to the mesher: shift the plate off the cell lattice now and then.
            double[] origin = P(0.5) ? [0, 0, 0] : [Math.Round(U(-1, 1), 2), Math.Round(U(-1, 1), 2), Math.Round(U(-1, 1), 2)];
            (_min, _max) = (origin.ToArray(), origin.Zip(size, (o, s) => o + s).ToArray());
            var plate = new Block("plate", BooleanOp.New, origin, size);
            _features.Add(plate);
            AddBox("plate", origin, _max.ToArray(), join: true);
            return plate;
        }

        Block BoxFeature(BooleanOp op)
        {
            var id = Id(op == BooleanOp.Join ? "boss" : "cut");
            double[] lo = new double[3], hi = new double[3];
            for (var a = 0; a < 3; a++)
            {
                var c = Coord(a);
                var through = op == BooleanOp.Cut && P(0.3);
                var len = through ? _max[a] - _min[a] + 4 : Length(0.6 * (_max[a] - _min[a]) + 2);
                if (through) c = _min[a] - 2;
                // A second snapped coordinate makes both sides of the block meet existing faces.
                var other = P(0.3) ? Coord(a) : c + (P(0.5) ? len : -len);
                (lo[a], hi[a]) = (Math.Min(c, other), Math.Max(c, other));
                if (hi[a] - lo[a] <= 1e-6) hi[a] = lo[a] + 1e-6;
            }
            var block = new Block(id, op, lo, hi.Zip(lo, (h, l) => h - l).ToArray());
            AddBox(id, lo, hi, op == BooleanOp.Join);
            return block;
        }

        Hole HoleFeature()
        {
            var id = Id("hole");
            var d = rng.Next(8) == 0 ? Of([0.2, 0.5, 0.9, 1.0, 1.1]) : Round(U(1, 10));
            double x, y;
            switch (rng.Next(5))
            {
                case 0 or 1: // tangent or nearly tangent to a side face
                    var axis = rng.Next(2);
                    var t = Of(_planes[axis]) + (P(0.5) ? 1 : -1) * (d / 2 + Nudge());
                    var o = U(_min[1 - axis], _max[1 - axis]);
                    (x, y) = axis == 0 ? (t, o) : (o, t);
                    break;
                case 2: // on a vertical edge
                    var corner = Of(_corners);
                    (x, y) = (corner[0] + Nudge(), corner[1] + Nudge());
                    break;
                default:
                    (x, y) = (Round(U(_min[0], _max[0])), Round(U(_min[1], _max[1])));
                    break;
            }
            _edges.Add(($"{_features[0].Id}/z+", $"{id}/wall"));
            _edges.Add(($"{_features[0].Id}/z-", $"{id}/wall"));
            _faces.Add($"{id}/wall");
            return new Hole(id, [x, y], d);
        }

        PlaneCut PlaneCutFeature()
        {
            var id = Id("slice");
            double[] point = rng.Next(3) switch
            {
                0 => Of(_corners).ToArray(), // through a vertex
                1 => [Coord(0), Coord(1), Coord(2)],
                _ => [U(_min[0], _max[0]), U(_min[1], _max[1]), U(_min[2], _max[2])],
            };
            double[] normal;
            if (P(0.3))
            {
                // Along an axis, or tilted off it by a hair: coincident and nearly coincident with a face.
                normal = [0, 0, 0];
                normal[rng.Next(3)] = P(0.5) ? 1 : -1;
                var tilt = Of([0, 0, 1e-9, 1e-6, 1e-3, 0.05]);
                normal[rng.Next(3)] += tilt;
            }
            else
            {
                normal = [U(-1, 1), U(-1, 1), U(-1, 1)];
                // Point the normal away from the part's middle mostly, so the cut trims rather than empties.
                var away = Enumerable.Range(0, 3).Sum(a => normal[a] * (point[a] - (_min[a] + _max[a]) / 2));
                if (away < 0 && P(0.8)) normal = normal.Select(v => -v).ToArray();
                normal = normal.Select(v => Math.Round(v, 3)).ToArray();
                if (normal.All(v => v == 0)) normal[2] = 1;
            }
            foreach (var f in _faces.Where(f => f.StartsWith(_features[0].Id + "/")).OrderBy(_ => rng.Next()).Take(3))
                _edges.Add((f, $"{id}/face"));
            _faces.Add($"{id}/face");
            return new PlaneCut(id, point, normal);
        }

        double Radius() => rng.Next(6) switch
        {
            0 => Of([0.1, 0.25, 0.4]), // below resolution
            1 => Of([0.5, 1, 2]) * (_max[2] - _min[2]), // as large as the plate is thick, or larger
            2 => 30,
            _ => Round(U(0.5, 4)),
        };

        (string, string) EdgePick() => P(0.85) || _faces.Count < 2 ? Of(_edges) : (Of(_faces), Of(_faces));

        public PartDocument Common()
        {
            Plate();
            var count = 1 + rng.Next(6);
            for (var i = 0; i < count; i++)
            {
                Feature f = rng.Next(20) switch
                {
                    < 5 => BoxFeature(BooleanOp.Join),
                    < 10 => BoxFeature(BooleanOp.Cut),
                    < 14 => HoleFeature(),
                    < 17 => PlaneCutFeature(),
                    _ => Fillet(),
                };
                _features.Add(f);
            }
            return new PartDocument(_features);
        }

        Fillet Fillet()
        {
            var (a, b) = EdgePick();
            return new Fillet(Id("round"), [a, b], Radius());
        }

        // ---- F-rep only ----

        static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);
        static readonly SketchPlane Xz = new([0, 0, 0], [1, 0, 0], [0, -1, 0]);

        static Profile Circle(double cx, double cy, double r) =>
            new([cx - r, cy], [new ProfileEdge("a", [cx + r, cy], 1), new ProfileEdge("b", [cx - r, cy], 1)]);

        /// <summary>A star-shaped polygon round the origin, some edges bulged into arcs.</summary>
        Profile Polygon(double cx, double cy, double r)
        {
            var n = 3 + rng.Next(6);
            var angles = Enumerable.Range(0, n).Select(k => (k + U(-0.3, 0.3)) * 2 * Math.PI / n).ToArray();
            var points = angles.Select(a => new[] { Math.Round(cx + r * U(0.6, 1) * Math.Cos(a), 3), Math.Round(cy + r * U(0.6, 1) * Math.Sin(a), 3) }).ToArray();
            var edges = Enumerable.Range(0, n)
                .Select(k => new ProfileEdge($"e{k}", points[(k + 1) % n], P(0.25) ? Math.Round(U(-0.4, 0.6), 3) : 0)).ToList();
            return new Profile(points[0], edges);
        }

        /// <summary>A stepped shaft profile for a revolve: x distance from the axis, y height.</summary>
        Profile Shaft()
        {
            var steps = 1 + rng.Next(4);
            var edges = new List<ProfileEdge>();
            double y = 0, r = Round(U(3, 15));
            edges.Add(new ProfileEdge("base", [r, 0]));
            for (var k = 0; k < steps; k++)
            {
                y += Round(U(2, 15));
                edges.Add(new ProfileEdge($"side{k}", [r, y], P(0.2) ? 0.2 : 0));
                var next = Math.Max(0.5, Round(r + U(-r + 0.5, 4)));
                if (k == steps - 1) next = P(0.5) ? 0 : next;
                edges.Add(new ProfileEdge($"step{k}", [next, y + (P(0.3) ? Round(U(0.5, 3)) : 0)]));
                (r, y) = (next, y + (edges[^1].To[1] - y));
            }
            if (r > 0) edges.Add(new ProfileEdge("top", [0, y]));
            edges.Add(new ProfileEdge("axis", [0, 0]));
            return new Profile([0, 0], edges);
        }

        public PartDocument FrepOnly()
        {
            switch (rng.Next(3))
            {
                case 0:
                    Plate();
                    break;
                case 1:
                    var depth = Round(U(3, 15));
                    var r = Round(U(8, 25));
                    _features.Add(new Extrude("body", BooleanOp.New, Xy, Polygon(0, 0, r), 0, depth));
                    (_min, _max) = ([-r, -r, 0], [r, r, depth]);
                    _planes[0].AddRange([-r, r]);
                    _planes[1].AddRange([-r, r]);
                    _planes[2].AddRange([0, depth]);
                    _corners.Add([r, 0, depth]);
                    _faces.AddRange(["body/start", "body/end", "body/e0", "body/e1"]);
                    _edges.AddRange([("body/end", "body/e0"), ("body/start", "body/e1"), ("body/e0", "body/e1")]);
                    break;
                default:
                    var shaft = Shaft();
                    var angle = P(0.7) ? 360 : Of([90.0, 180, 270, 45]);
                    _features.Add(new Revolve("body", BooleanOp.New, Xz, shaft, angle));
                    var (rr, hh) = (shaft.Edges.Max(e => e.To[0]), shaft.Edges.Max(e => e.To[1]));
                    (_min, _max) = ([-rr, -rr, 0], [rr, rr, hh]);
                    _planes[0].AddRange([-rr, rr]);
                    _planes[1].AddRange([-rr, rr]);
                    _planes[2].AddRange([0, hh]);
                    _corners.Add([rr, 0, 0]);
                    _faces.AddRange(shaft.Edges.Select(e => $"body/{e.Id}"));
                    for (var k = 0; k + 1 < shaft.Edges.Count; k++) _edges.Add(($"body/{shaft.Edges[k].Id}", $"body/{shaft.Edges[k + 1].Id}"));
                    break;
            }

            var count = 1 + rng.Next(5);
            var shelled = false;
            for (var i = 0; i < count; i++)
            {
                var top = new SketchPlane([0, 0, _max[2]], [1, 0, 0], [0, 0, 1]);
                Feature? f = rng.Next(26) switch
                {
                    < 2 => BoxFeature(BooleanOp.Join) with { Blend = P(0.5) ? Round(U(0.5, 3)) : 0 },
                    < 4 => BoxFeature(BooleanOp.Cut) with { Blend = P(0.5) ? Round(U(0.5, 3)) : 0 },
                    < 5 => HoleFeature(),
                    < 6 => PlaneCutFeature(),
                    < 8 => SketchBoss(top),
                    < 9 => new Revolve(Id("bore"), BooleanOp.Cut, new SketchPlane([Round(U(_min[0], _max[0])), Round(U(_min[1], _max[1])), _min[2] - 1], [1, 0, 0], [0, -1, 0]),
                        new Profile([0, 0], [new ProfileEdge("bottom", [Round(U(0.5, 4)), 0]), new ProfileEdge("wall", [Round(U(0.5, 4)), _max[2] - _min[2] + 2]),
                            new ProfileEdge("top", [0, _max[2] - _min[2] + 2]), new ProfileEdge("axis", [0, 0])])),
                    < 12 => PatternOf(),
                    < 15 => Fillet(),
                    < 17 => new Fillet(Id("loop"), [], Radius(), Loop: Of(_faces)),
                    < 19 => Chamfer(),
                    < 20 => new Chamfer(Id("bevels"), [], Radius(), Loop: Of(_faces)),
                    < 22 when !shelled => new Shell(Id("shell"), Of([0.3, 0.8, 1.0, 1.5, 2.5, 5.0])),
                    < 23 when !shelled => new Offset(Id("grow"), Of([-1.5, -0.5, 0.3, 1.0, 2.0])),
                    < 25 => SecondBody(),
                    _ => null,
                };
                shelled |= f is Shell;
                if (f is not null) _features.Add(f);
            }
            return new PartDocument(_features);
        }

        Chamfer Chamfer()
        {
            var (a, b) = EdgePick();
            return new Chamfer(Id("bevel"), [a, b], Radius());
        }

        Extrude SketchBoss(SketchPlane top)
        {
            var id = Id("knob");
            var (cx, cy) = (Round(U(_min[0], _max[0])), Round(U(_min[1], _max[1])));
            var profile = P(0.5) ? Circle(cx, cy, Length(8)) : Polygon(cx, cy, Round(U(1, 8)));
            var cut = P(0.4);
            var e = cut
                ? new Extrude(id, BooleanOp.Cut, top, profile, -Round(U(0.5, _max[2] - _min[2] + 1)), 1)
                : new Extrude(id, BooleanOp.Join, top, profile, P(0.2) ? -Of([1e-6, 0.0, 0.5]) : 0, Round(U(1, 12)), P(0.3) ? Round(U(0.5, 3)) : 0);
            _faces.AddRange([$"{id}/end", $"{id}/start", $"{id}/{profile.Edges[0].Id}"]);
            _edges.Add(($"{id}/end", $"{id}/{profile.Edges[0].Id}"));
            _edges.Add(($"{_features[0].Id}/{(_features[0] is Block ? "z+" : "end")}", $"{id}/{profile.Edges[0].Id}"));
            if (!cut) _max[2] += e.End;
            return e;
        }

        Feature? PatternOf()
        {
            var sources = _features.Skip(1).Where(f => f is Block or Hole or PlaneCut or Extrude or Revolve).ToList();
            if (sources.Count == 0) return null;
            var id = Id("row");
            var copies = new[] { Of(sources).Id };
            PatternMotion motion = rng.Next(3) switch
            {
                0 => new LinearMotion(P(0.7) ? [1, 0, 0] : [Math.Round(U(-1, 1), 3), Math.Round(U(-1, 1), 3), 0.001], Of([0.5, 1, 3, 5, 8.0]) * (P(0.5) ? 1 : -1), 2 + rng.Next(5)),
                1 => new CircularMotion([Round((_min[0] + _max[0]) / 2), Round((_min[1] + _max[1]) / 2), 0], [0, 0, 1], 2 + rng.Next(8), P(0.6) ? 360 : Of([30.0, 90, 180])),
                _ => new MirrorMotion([Round((_min[0] + _max[0]) / 2 + Nudge()), 0, 0], [1, 0, 0]),
            };
            return new Pattern(id, copies, motion);
        }

        Feature SecondBody()
        {
            var id = Id("part");
            var lo = new[] { Coord(0), Coord(1), Coord(2) };
            var block = new Block(id, BooleanOp.New, lo, [Length(15), Length(15), Length(10)]);
            AddBox(id, lo, lo.Zip(block.Size, (l, s) => l + s).ToArray(), join: false);
            _features.Add(block);
            // Usually combine it straight back into the first body.
            var first = _features[0].Id;
            return P(0.7) ? new CombineBodies(Id("combine"), id, P(0.5) ? BooleanOp.Join : BooleanOp.Cut, Keep: P(0.3)) { Body = first }
                : new Hole(Id("hole"), [lo[0] + block.Size[0] / 2, lo[1] + block.Size[1] / 2], Math.Min(block.Size[0], block.Size[1]) / 2);
        }
    }
}
