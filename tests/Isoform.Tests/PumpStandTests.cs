using System.Diagnostics;
using BasAutomation.Geometry;
using Isoform.Interactive;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>The design model of the end-to-end demo (docs/demo-plan.md §1), regenerated once at 1 mm.</summary>
public class PumpStandTests(PumpStandTests.Regenerated stand, ITestOutputHelper output) : IClassFixture<PumpStandTests.Regenerated>
{
    public sealed class Regenerated
    {
        public FrepResult Result { get; }
        public TimeSpan Time { get; }

        public Regenerated()
        {
            var clock = Stopwatch.StartNew();
            Result = new FrepModeler(new ModelingSettings { CellSize = 1 }).Regenerate(Examples.PumpStand);
            Time = clock.Elapsed;
        }
    }

    FrepResult Result => stand.Result;

    IEnumerable<string[]> FacesOf(string source) => Result.Topology!.Faces.Select(f => f.Sources.ToArray()).Where(s => s.Contains(source));

    static IEnumerable<string> WithCopies(string pattern, string source) => [source, .. Enumerable.Range(1, 3).Select(k => $"{pattern}/{k}:{source}")];

    [Fact]
    public void Regenerates_at_1_mm_without_diagnostics()
    {
        output.WriteLine($"regenerated in {stand.Time.TotalMilliseconds:N0} ms (first run, with JIT): {Result.Bodies[0].Faces.Count} faces on the stand, " +
            $"{Result.Topology!.Faces.Count} with the keep-outs, {Result.Mesh!.Quads.Count:N0} polygons");

        Result.Diagnostics.Should().BeEmpty();
        Result.Bodies.Select(b => b.Name).Should().Equal("stand", "pump", "hose", "socket-1", "socket-2", "socket-3", "socket-4");
        Result.Topology.BodyCount.Should().Be(Result.Bodies.Count);
        Result.Mesh.IsWatertight().Should().BeTrue();
    }

    [Fact]
    public void Has_the_faces_the_build_binds_to()
    {
        // The pump seat and the top's underside are one flat face each.
        FacesOf("cap/face").Should().ContainSingle();
        FacesOf("under/face").Should().ContainSingle();
        FacesOf("bore/wall").Should().ContainSingle();
        foreach (var name in WithCopies("mounts", "mount/wall").Concat(WithCopies("feet", "anchor/wall")).Concat(WithCopies("feet", "foot/z-")))
            FacesOf(name).Should().ContainSingle(name);
        // Each leg blends into the top and into its foot: two blend faces.
        foreach (var name in WithCopies("legs", "leg/blend"))
            FacesOf(name).Should().HaveCount(2, name);
        // Each lobe meets the disc on two sides.
        foreach (var name in WithCopies("lobes", "lobe/blend"))
            FacesOf(name).Should().HaveCount(2, name);
    }

    [Fact]
    public void Keeps_out_of_the_keep_outs()
    {
        var part = Result.Part;
        var body = part.Bodies.ToDictionary(b => b.Name);
        var clock = Stopwatch.StartNew();
        ClearanceResult Clearance(string keepOut) =>
            Queries.Clearance(body["stand"].Field, new Box3D(body["stand"].Min, body["stand"].Max), body[keepOut].Field, new Box3D(body[keepOut].Min, body[keepOut].Max), 1e-3);

        // The pump sits on the top face and the sockets on the feet: contact, no interference.
        var pump = Clearance("pump");
        pump.Interferes.Should().BeFalse();
        pump.Upper.Should().BeLessThan(1e-3);
        foreach (var socket in Enumerable.Range(1, 4).Select(k => $"socket-{k}"))
        {
            var c = Clearance(socket);
            c.Interferes.Should().BeFalse(socket);
            c.Upper.Should().BeLessThan(1e-3, socket);
        }
        // The hose passes the Ø80 bore 10 mm clear (the build asks 5).
        var hose = Clearance("hose");
        hose.Lower.Should().BeGreaterThan(5);
        hose.Upper.Should().BeApproximately(10, 1e-6);
        output.WriteLine($"keep-out clearances in {clock.Elapsed.TotalMilliseconds:N0} ms: pump [{pump.Lower:G3}, {pump.Upper:G3}], hose [{hose.Lower:G6}, {hose.Upper:G6}]");
    }
}
