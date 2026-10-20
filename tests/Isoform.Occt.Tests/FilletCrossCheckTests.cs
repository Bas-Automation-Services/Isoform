using Isoform.Document;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Occt.Tests;

/// <summary>H4 against OCCT: our rolling-ball fillets are the same analytic surfaces OCCT's fillet builds.</summary>
public class FilletCrossCheckTests(ITestOutputHelper output)
{
    const double Tolerance = 1e-7;
    const double R = 2;

    [Theory]
    [InlineData(90)]
    [InlineData(60)]
    [InlineData(120)]
    public void Plane_plane_fillet_matches_occt_cylinder(double dihedral)
    {
        var tilt = (180 - dihedral) * Math.PI / 180;
        double[] normal = [Math.Sin(tilt), 0, Math.Cos(tilt)];
        var doc = new PartDocument(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]),
            new PlaneCut("cut", [90, 0, 10], normal),
            new Fillet("fillet", ["plate/z+", "cut/face"], R),
        ]);

        var face = OcctFilletFace(doc);

        // Our ball centre line: depth r below both planes, along y. In the xz-plane: centre = edge − (nTop + nCut)·r/(1 + c).
        var c = normal[2];
        var cx = 90 - normal[0] * R / (1 + c);
        var cz = 10 - (1 + normal[2]) * R / (1 + c);
        face.Kind.Should().Be(OcctFaceKind.Cylinder);
        var d = face.Data;
        output.WriteLine($"{dihedral}°: OCCT axis through ({d[0]:F9}, {d[2]:F9}) dir ({d[3]:F6},{d[4]:F6},{d[5]:F6}) r {d[6]:F9}; ours ({cx:F9}, {cz:F9})");
        d[6].Should().BeApproximately(R, Tolerance);
        Math.Abs(d[4]).Should().BeApproximately(1, Tolerance, "axis runs along the edge (y)");
        d[0].Should().BeApproximately(cx, Tolerance);
        d[2].Should().BeApproximately(cz, Tolerance);
    }

    [Fact]
    public void Hole_edge_fillet_matches_occt_torus()
    {
        var doc = new PartDocument(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]),
            new Hole("hole", [30, 25], 10),
            new Fillet("fillet", ["plate/z+", "hole/wall"], R),
        ]);

        var face = OcctFilletFace(doc);

        face.Kind.Should().Be(OcctFaceKind.Torus);
        var d = face.Data;
        output.WriteLine($"torus: centre ({d[0]:F9}, {d[1]:F9}, {d[2]:F9}) major {d[6]:F9} minor {d[7]:F9}");
        (d[0], d[1], d[2]).Should().Be((30.0, 25.0, 10 - R));
        d[6].Should().BeApproximately(5 + R, Tolerance);
        d[7].Should().BeApproximately(R, Tolerance);
    }

    static OcctFace OcctFilletFace(PartDocument doc)
    {
        var result = new OcctModeler(new ModelingSettings()).Regenerate(doc);
        result.Diagnostics.Should().BeEmpty();
        var face = result.Topology!.Faces.Single(f => f.Sources.Contains("fillet/face"));
        return result.Shape!.Face(face.Index);
    }
}
