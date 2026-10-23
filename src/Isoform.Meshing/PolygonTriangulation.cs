using System.Numerics;

namespace Isoform.Meshing;

/// <summary>
/// Polygons with holes into triangles, by ear clipping with hole bridges (after Eberly, as in mapbox's earcut).
/// Orientation tests are exact, so every triangle turns anticlockwise and none overlaps another; a point that is
/// on a triangle, its edges included, keeps it from being clipped, so no vertex ends up on another triangle's edge.
/// Deterministic.
/// </summary>
public static class PolygonTriangulation
{
    /// <summary>
    /// Three point indices per triangle, or null when the loops do not triangulate. Outer loops turn anticlockwise,
    /// holes clockwise; loops may share points (a polygon pinched to a point), never edges. Points closer than
    /// <paramref name="tolerance"/> to a line count as on it: no triangle is thinner, none has a point that near its edges.
    /// No diagonal joins two points <paramref name="apart"/> says must stay apart.
    /// </summary>
    public static int[]? Triangulate(double[] x, double[] y, IReadOnlyList<int[]> loops, double tolerance = 0, Func<int, int, bool>? apart = null)
    {
        var area = loops.Select(l => Area(x, y, l)).ToArray();
        if (area.Any(a => a == 0) || loops.Any(l => l.Length < 3)) return null;
        var outers = Enumerable.Range(0, loops.Count).Where(i => area[i] > 0).ToList();
        var holesOf = outers.ToDictionary(o => o, _ => new List<int>());
        foreach (var h in Enumerable.Range(0, loops.Count).Where(i => area[i] < 0))
        {
            // The midpoint of an edge of the hole is inside the outer loop that holds it, never on it.
            var l = loops[h];
            var (px, py) = ((x[l[0]] + x[l[1]]) / 2, (y[l[0]] + y[l[1]]) / 2);
            var holder = outers.Where(o => Inside(x, y, loops[o], px, py)).OrderBy(o => area[o]).ThenBy(o => o).FirstOrDefault(-1);
            if (holder < 0) return null;
            holesOf[holder].Add(h);
        }

        var ring = new Ring(x, y, tolerance, apart);
        var triangles = new List<int>();
        foreach (var o in outers)
        {
            var start = ring.Add(loops[o]);
            var holes = holesOf[o].Select(h => ring.Leftmost(ring.Add(loops[h]))).OrderBy(n => x[ring.P[n]]).ThenBy(n => y[ring.P[n]]).ToList();
            foreach (var hole in holes)
                if (!ring.Bridge(hole, start)) return null;
            if (!ring.Clip(ring.Filter(start), triangles)) return null;
        }
        return [.. triangles];
    }

    /// <summary>Twice the signed area of a loop.</summary>
    static double Area(double[] x, double[] y, int[] loop)
    {
        var sum = 0.0;
        for (var i = 0; i < loop.Length; i++)
        {
            var (a, b) = (loop[i], loop[(i + 1) % loop.Length]);
            sum += x[a] * y[b] - x[b] * y[a];
        }
        return sum;
    }

    static bool Inside(double[] x, double[] y, int[] loop, double px, double py)
    {
        var inside = false;
        for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
        {
            var (a, b) = (loop[i], loop[j]);
            if ((y[a] > py) != (y[b] > py) && px < (x[b] - x[a]) * (py - y[a]) / (y[b] - y[a]) + x[a]) inside = !inside;
        }
        return inside;
    }

    /// <summary>The sign of the turn a → b → c: +1 anticlockwise, -1 clockwise, 0 on one line. Exact.</summary>
    public static int Orient(double ax, double ay, double bx, double by, double cx, double cy)
    {
        var (l, r) = ((bx - ax) * (cy - ay), (by - ay) * (cx - ax));
        var det = l - r;
        var bound = 3.3306690738754716e-16 * (Math.Abs(l) + Math.Abs(r));
        if (det > bound) return 1;
        if (-det > bound) return -1;
        // Too close to call in floating point: integers on a common binary exponent.
        Span<double> v = [ax, ay, bx, by, cx, cy];
        var e = int.MaxValue;
        foreach (var d in v) e = Math.Min(e, Decompose(d).Exponent);
        BigInteger I(double d)
        {
            var (m, de) = Decompose(d);
            return new BigInteger(m) << (de - e);
        }
        var (iax, iay, ibx, iby, icx, icy) = (I(ax), I(ay), I(bx), I(by), I(cx), I(cy));
        return ((ibx - iax) * (icy - iay) - (iby - iay) * (icx - iax)).Sign;
    }

    /// <summary>d = Mantissa · 2^Exponent.</summary>
    static (long Mantissa, int Exponent) Decompose(double d)
    {
        var bits = BitConverter.DoubleToInt64Bits(d);
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0) exponent = 1;
        else mantissa |= 1L << 52;
        return (bits < 0 ? -mantissa : mantissa, exponent - 1075);
    }

    /// <summary>Doubly linked rings of nodes, each a point; bridges copy their two end nodes.</summary>
    sealed class Ring(double[] x, double[] y, double tolerance, Func<int, int, bool>? apart)
    {
        public readonly List<int> P = [], Prev = [], Next = [];

        public int Add(int[] loop)
        {
            var first = P.Count;
            for (var i = 0; i < loop.Length; i++)
            {
                P.Add(loop[i]);
                Prev.Add(first + (i + loop.Length - 1) % loop.Length);
                Next.Add(first + (i + 1) % loop.Length);
            }
            return first;
        }

        int Copy(int n)
        {
            P.Add(P[n]);
            Prev.Add(-1);
            Next.Add(-1);
            return P.Count - 1;
        }

        double X(int n) => x[P[n]];
        double Y(int n) => y[P[n]];
        int Orient(int a, int b, int c) => PolygonTriangulation.Orient(X(a), Y(a), X(b), Y(b), X(c), Y(c));

        public int Leftmost(int start)
        {
            var (best, n) = (start, Next[start]);
            for (; n != start; n = Next[n])
                if (X(n) < X(best) || (X(n) == X(best) && Y(n) < Y(best))) best = n;
            return best;
        }

        /// <summary>Joins a hole into the ring of <paramref name="outer"/> along a bridge both ways.</summary>
        public bool Bridge(int hole, int outer)
        {
            var (from, to) = Shared(hole, outer);
            if (to < 0) (from, to) = (hole, Visible(hole, outer));
            if (to < 0) return false;
            Split(to, from);
            return true;
        }

        /// <summary>A hole that touches the ring in a point joins it there: a node of each on that point, or -1.</summary>
        (int Hole, int Outer) Shared(int hole, int outer)
        {
            var points = new Dictionary<int, int>();
            var n = outer;
            do
            {
                points.TryAdd(P[n], n);
                n = Next[n];
            }
            while (n != outer);
            n = hole;
            do
            {
                if (points.TryGetValue(P[n], out var at)) return (n, at);
                n = Next[n];
            }
            while (n != hole);
            return (-1, -1);
        }

        /// <summary>The ring node a bridge from the hole's leftmost node can reach without crossing anything (earcut's findHoleBridge).</summary>
        int Visible(int hole, int outer)
        {
            var (hx, hy) = (X(hole), Y(hole));
            var (qx, m, p) = (double.NegativeInfinity, -1, outer);
            do
            {
                var q = Next[p];
                if (hy <= Y(p) && hy >= Y(q) && Y(q) != Y(p))
                {
                    var cross = X(p) + (hy - Y(p)) * (X(q) - X(p)) / (Y(q) - Y(p));
                    if (cross <= hx && cross > qx)
                    {
                        qx = cross;
                        m = X(p) < X(q) ? p : q;
                        if (cross == hx) return m;
                    }
                }
                p = q;
            }
            while (p != outer);
            if (m < 0) return -1;
            var (stop, mx, my, tanMin) = (m, X(m), Y(m), double.PositiveInfinity);
            p = m;
            do
            {
                if (hx >= X(p) && X(p) >= mx && hx != X(p) && InTriangle(hy < my ? hx : qx, hy, mx, my, hy < my ? qx : hx, hy, X(p), Y(p)))
                {
                    var tan = Math.Abs(hy - Y(p)) / (hx - X(p));
                    if (LocallyInside(p, hole) && (tan < tanMin || (tan == tanMin && (X(p) > X(m) || (X(p) == X(m) && SectorContainsSector(m, p))))))
                        (m, tanMin) = (p, tan);
                }
                p = Next[p];
            }
            while (p != stop);
            return m;
        }

        static bool InTriangle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py)
        {
            var (o1, o2, o3) = (PolygonTriangulation.Orient(ax, ay, bx, by, px, py), PolygonTriangulation.Orient(bx, by, cx, cy, px, py), PolygonTriangulation.Orient(cx, cy, ax, ay, px, py));
            return (o1 >= 0 && o2 >= 0 && o3 >= 0) || (o1 <= 0 && o2 <= 0 && o3 <= 0);
        }

        bool LocallyInside(int a, int b) => Orient(Prev[a], a, Next[a]) > 0
            ? Orient(a, b, Next[a]) <= 0 && Orient(a, Prev[a], b) <= 0
            : Orient(a, b, Prev[a]) > 0 || Orient(a, Next[a], b) > 0;

        bool SectorContainsSector(int m, int p) => Orient(Prev[m], m, Prev[p]) > 0 && Orient(Next[p], m, Next[m]) > 0;

        /// <summary>Links a to b and back again through copies of both: earcut's splitPolygon.</summary>
        void Split(int a, int b)
        {
            var (a2, b2) = (Copy(a), Copy(b));
            var (an, bp) = (Next[a], Prev[b]);
            (Next[a], Prev[b]) = (b, a);
            (Next[a2], Prev[an]) = (an, a2);
            (Next[b2], Prev[a2]) = (a2, b2);
            (Next[bp], Prev[b2]) = (b2, bp);
        }

        void Remove(int n) => (Next[Prev[n]], Prev[Next[n]]) = (Next[n], Prev[n]);

        /// <summary>Drops nodes on the same point as the next one (a bridge to a shared point); a node of the ring.</summary>
        public int Filter(int start)
        {
            for (var again = true; again;)
            {
                again = false;
                var n = start;
                do
                {
                    if (P[n] == P[Next[n]] && Next[n] != n)
                    {
                        Remove(n);
                        (start, again) = (Next[n], true);
                        break;
                    }
                    n = Next[n];
                }
                while (n != start);
            }
            return start;
        }

        bool IsEar(int ear)
        {
            var (a, c) = (Prev[ear], Next[ear]);
            if (Orient(a, ear, c) <= 0 || Thin(a, ear, c) || (Next[c] != a && apart is not null && apart(P[a], P[c]))) return false;
            var (pa, pb, pc) = (P[a], P[ear], P[c]);
            var (minX, maxX) = (Math.Min(X(a), Math.Min(X(ear), X(c))) - tolerance, Math.Max(X(a), Math.Max(X(ear), X(c))) + tolerance);
            var (minY, maxY) = (Math.Min(Y(a), Math.Min(Y(ear), Y(c))) - tolerance, Math.Max(Y(a), Math.Max(Y(ear), Y(c))) + tolerance);
            for (var p = Next[c]; p != a; p = Next[p])
            {
                var pp = P[p];
                if (pp == pa || pp == pb || pp == pc) continue;
                var (px, py) = (x[pp], y[pp]);
                if (px < minX || px > maxX || py < minY || py > maxY) continue;
                if (Orient(a, ear, p) >= 0 && Orient(ear, c, p) >= 0 && Orient(c, a, p) >= 0) return false;
                if (OnSegment(a, ear, p) || OnSegment(ear, c, p) || OnSegment(c, a, p)) return false;
            }
            return true;
        }

        // A triangle less than the tolerance high, or a point within it of a segment: on one line.
        bool Thin(int a, int b, int c)
        {
            var (ab, bc, ca) = (Length(a, b), Length(b, c), Length(c, a));
            var twice = Math.Abs((X(b) - X(a)) * (Y(c) - Y(a)) - (Y(b) - Y(a)) * (X(c) - X(a)));
            return twice <= tolerance * Math.Max(ab, Math.Max(bc, ca));
        }

        bool OnSegment(int a, int b, int p)
        {
            var (dx, dy) = (X(b) - X(a), Y(b) - Y(a));
            var (px, py) = (X(p) - X(a), Y(p) - Y(a));
            var t = Math.Clamp((px * dx + py * dy) / (dx * dx + dy * dy), 0, 1);
            return Math.Sqrt((px - t * dx) * (px - t * dx) + (py - t * dy) * (py - t * dy)) <= tolerance;
        }

        double Length(int a, int b) => Math.Sqrt((X(b) - X(a)) * (X(b) - X(a)) + (Y(b) - Y(a)) * (Y(b) - Y(a)));

        public bool Clip(int ear, List<int> into)
        {
            var stop = ear;
            while (Prev[ear] != Next[ear])
            {
                var (prev, next) = (Prev[ear], Next[ear]);
                if (IsEar(ear))
                {
                    into.Add(P[prev]);
                    into.Add(P[ear]);
                    into.Add(P[next]);
                    Remove(ear);
                    ear = stop = Next[next];
                    continue;
                }
                ear = next;
                if (ear == stop) return false;
            }
            return true;
        }
    }
}
