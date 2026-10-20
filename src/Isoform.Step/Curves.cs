using BasAutomation.Geometry;

namespace Isoform.Step;

/// <summary>An exact edge curve, parametrised by <c>t</c>.</summary>
public abstract record Curve
{
    public abstract Point3D At(double t);

    public abstract Vector3D Tangent(double t);

    /// <summary>The parameter of the curve point nearest to <paramref name="p"/>.</summary>
    public abstract double Parameter(Point3D p);
}

/// <summary>Origin + t · Direction, with a unit direction.</summary>
public sealed record Line(Point3D Origin, Vector3D Direction) : Curve
{
    public override Point3D At(double t) => Origin + Direction * t;
    public override Vector3D Tangent(double t) => Direction;
    public override double Parameter(Point3D p) => Direction.Dot(p - Origin);
}

/// <summary>A circle counter-clockwise round <see cref="Normal"/>, starting at <see cref="X"/>; t is the angle.</summary>
public sealed record Circle(Point3D Center, Vector3D Normal, Vector3D X, double Radius) : Curve
{
    public Vector3D Y => Normal.Cross(X);
    public override Point3D At(double t) => Center + (X * Math.Cos(t) + Y * Math.Sin(t)) * Radius;
    public override Vector3D Tangent(double t) => (Y * Math.Cos(t) - X * Math.Sin(t)) * Radius;
    public override double Parameter(Point3D p) => Math.Atan2(Y.Dot(p - Center), X.Dot(p - Center));
}

/// <summary>An ellipse counter-clockwise round <see cref="Normal"/>, its major axis along <see cref="X"/>.</summary>
public sealed record Ellipse(Point3D Center, Vector3D Normal, Vector3D X, double Major, double Minor) : Curve
{
    public Vector3D Y => Normal.Cross(X);
    public override Point3D At(double t) => Center + X * (Major * Math.Cos(t)) + Y * (Minor * Math.Sin(t));
    public override Vector3D Tangent(double t) => Y * (Minor * Math.Cos(t)) - X * (Major * Math.Sin(t));
    public override double Parameter(Point3D p) => Math.Atan2(Y.Dot(p - Center) / Minor, X.Dot(p - Center) / Major);
}
