using BasAutomation.Geometry;

namespace Isoform.Brep.Tests;

/// <summary>Profiles used across the tests, with their exact areas.</summary>
static class Shapes
{
    public static Point2D P(double x, double y) => new(x, y);

    public static ProfileLoop Rectangle(double x0, double y0, double x1, double y1) => new(
    [
        new LineSegment(P(x0, y0), P(x1, y0)),
        new LineSegment(P(x1, y0), P(x1, y1)),
        new LineSegment(P(x1, y1), P(x0, y1)),
        new LineSegment(P(x0, y1), P(x0, y0)),
    ]);

    public static ProfileLoop Circle(double x, double y, double r) => new([ArcSegment.Circle(P(x, y), r)]);

    /// <summary>A slot: two straight sides of length <paramref name="length"/> joined by half circles of radius r.</summary>
    public static ProfileLoop Slot(double length, double r) => new(
    [
        new LineSegment(P(0, -r), P(length, -r)),
        new ArcSegment(P(length, 0), r, -Math.PI / 2, Math.PI),
        new LineSegment(P(length, r), P(0, r)),
        new ArcSegment(P(0, 0), r, Math.PI / 2, Math.PI),
    ]);

    /// <summary>
    /// A 60 × 40 rectangle with a concave bite of radius 10 out of its top side (a clockwise arc), and a rounded
    /// convex corner of radius 5. Area 60·40 − π·10²/2 − (5² − π·5²/4).
    /// </summary>
    public static ProfileLoop Bitten() => new(
    [
        new LineSegment(P(0, 0), P(55, 0)),
        new ArcSegment(P(55, 5), 5, -Math.PI / 2, Math.PI / 2),
        new LineSegment(P(60, 5), P(60, 40)),
        new LineSegment(P(60, 40), P(40, 40)),
        new ArcSegment(P(30, 40), 10, 0, -Math.PI),
        new LineSegment(P(20, 40), P(0, 40)),
        new LineSegment(P(0, 40), P(0, 0)),
    ]);

    public static readonly double BittenArea = 60 * 40 - Math.PI * 100 / 2 - (25 - Math.PI * 25 / 4);

    /// <summary>A D shape: a line and a half circle, two segments sharing both ends.</summary>
    public static ProfileLoop D(double x, double y, double r) => new(
    [
        new LineSegment(P(x, y + r), P(x, y - r)),
        new ArcSegment(P(x, y), r, -Math.PI / 2, Math.PI),
    ]);
}
