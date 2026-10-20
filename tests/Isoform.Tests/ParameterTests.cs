using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Modeling;

namespace Isoform.Tests;

/// <summary>Part parameters: expressions, their canonical form, edits, diagnostics and assembly variants.</summary>
public class ParameterTests
{
    static readonly FrepModeler Modeler = new(new ModelingSettings { CellSize = 0.5 });

    static double Eval(string text) => Expression.Evaluate(text, name => name switch
    {
        "w" => 100,
        "d" => 4,
        _ => throw new FormatException($"unknown parameter '{name}'"),
    });

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 - 4 - 3", 3)]
    [InlineData("12 / 3 / 2", 2)]
    [InlineData("-2^2", -4)]
    [InlineData("2^3^2", 512)]
    [InlineData("2^-1", 0.5)]
    [InlineData("--3", 3)]
    [InlineData("w / 2 + 3", 53)]
    [InlineData("-w*d", -400)]
    [InlineData("1.5e2 + .5", 150.5)]
    [InlineData("min(d - 4, 10, 3)", 0)]
    [InlineData("max(1, w)", 100)]
    [InlineData("abs(-2.5)", 2.5)]
    [InlineData("sqrt(16)", 4)]
    [InlineData("round(2.5) + round(-2.5)", 0)]
    [InlineData("round(2.4)", 2)]
    [InlineData("floor(-1.5)", -2)]
    [InlineData("ceil(1.2)", 2)]
    [InlineData("cos(90) + sin(180) + sin(-360)", 0)]
    [InlineData("sin(90) + cos(0) - cos(180)", 3)]
    [InlineData("sin(-90) + cos(270)", -1)]
    public void Expressions_follow_precedence_and_functions(string text, double expected) =>
        Eval(text).Should().Be(expected);

    [Fact]
    public void Trigonometry_is_in_degrees()
    {
        Eval("sin(30)").Should().BeApproximately(0.5, 1e-15);
        Eval("tan(45)").Should().BeApproximately(1, 1e-15);
        Eval("cos(60)").Should().BeApproximately(0.5, 1e-15);
        Eval("2 * pi").Should().Be(2 * Math.PI);
    }

    [Theory]
    [InlineData("1 +", "ends early")]
    [InlineData("(1 + 2", "missing ')'")]
    [InlineData("1 2", "unexpected '2'")]
    [InlineData("2 $ 3", "unexpected '$'")]
    [InlineData("1e", "unexpected 'e'")]
    [InlineData("1.2.3", "not a number")]
    [InlineData("foo(1)", "unknown function 'foo'")]
    [InlineData("width", "unknown parameter 'width'")]
    [InlineData("sqrt(-1)", "sqrt of a negative")]
    [InlineData("sqrt(1, 2)", "takes one argument")]
    [InlineData("min()", "at least one argument")]
    [InlineData("1 / (d - 4)", "division by zero")]
    [InlineData("tan(90)", "undefined")]
    [InlineData("10^400", "not a finite number")]
    public void Bad_expressions_say_why(string text, string message) =>
        FluentActions.Invoking(() => Eval(text)).Should().Throw<FormatException>().Where(e => e.Message.Contains(message));

    // A plate whose holes and pattern follow its parameters; one field of each kind holds an expression.
    const string PlateText = """
        {
          "format": "isoform/0",
          "units": "mm",
          "parameters": {
            "bore": "=min(depth - 4, 10)",
            "depth": "=width / 2",
            "holes": 3,
            "width": 100
          },
          "features": [
            {
              "id": "plate",
              "type": "block",
              "op": "new",
              "origin": [
                0,
                0,
                0
              ],
              "size": [
                "=width",
                "=depth",
                10
              ]
            },
            {
              "id": "hole",
              "type": "hole",
              "center": [
                "=width / 4",
                "=depth / 2"
              ],
              "diameter": "=bore"
            },
            {
              "id": "row",
              "type": "pattern",
              "features": [
                "hole"
              ],
              "linear": {
                "direction": [
                  1,
                  0,
                  0
                ],
                "spacing": "=width / (2 * (holes - 1))",
                "count": "=holes"
              }
            },
            {
              "id": "boss",
              "type": "block",
              "op": "join",
              "origin": [
                "=width / 2 - 5",
                "=depth - 14",
                10
              ],
              "size": [
                10,
                6,
                5
              ],
              "blend": "=bore / 5"
            }
          ]
        }

        """;

    static PartDocument Plate => CanonicalFormat.Read(PlateText);

    [Fact]
    public void Canonical_text_keeps_the_expressions_byte_for_byte()
    {
        var text = PlateText.ReplaceLineEndings("\n");
        var doc = CanonicalFormat.Read(text);

        CanonicalFormat.Write(doc).Should().Be(text);
        doc.Parameters.Should().HaveCount(4);
        doc.Expressions["hole.diameter"].Should().Be("bore");
        doc.Expressions["row.linear.count"].Should().Be("holes");
    }

    [Fact]
    public void Resolving_gives_plain_numbers_the_compiler_takes_unchanged()
    {
        var resolved = Parametric.Resolve(Plate);

        Parametric.IsParametric(resolved).Should().BeFalse();
        ((Block)resolved["plate"]).Size.Should().Equal(100, 50, 10);
        ((Hole)resolved["hole"]).Center.Should().Equal(25, 25);
        ((Hole)resolved["hole"]).Diameter.Should().Be(10);
        ((LinearMotion)((Pattern)resolved["row"]).Motion).Should().Match<LinearMotion>(l => l.Count == 3 && l.Spacing == 25);
        ((Block)resolved["boss"]).Blend.Should().Be(2);
        CanonicalFormat.Write(resolved).Should().NotContain("=").And.NotContain("parameters");
    }

    [Fact]
    public void Documents_without_parameters_keep_their_bytes_and_resolve_to_themselves()
    {
        var doc = EditSuite.Base;

        Parametric.Resolve(doc).Should().BeSameAs(doc);
        CanonicalFormat.Write(doc).Should().NotContain("parameters");
    }

    [Fact]
    public void The_hash_covers_the_expressions_not_just_their_values()
    {
        var a = Plate;
        var b = DocumentEdits.Apply(a, [Set("hole.diameter", "=10")]);

        Parametric.Resolve(b).Should().BeEquivalentTo(Parametric.Resolve(a), o => o.RespectingRuntimeTypes());
        CanonicalFormat.Hash(b).Should().NotBe(CanonicalFormat.Hash(a));
        CanonicalFormat.Hash(Parametric.Resolve(b)).Should().Be(CanonicalFormat.Hash(Parametric.Resolve(a)));
        // A constant expression is stored as the number it is.
        CanonicalFormat.Write(b).Should().Contain("\"diameter\": 10\n");
    }

    [Fact]
    public void Expressions_built_in_code_land_where_the_text_puts_them()
    {
        var boss = new Block("boss", BooleanOp.Join, [40, 15, 10], [20, 20, 10]) { Body = "main" };
        var doc = new PartDocument([new Block("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]) { Body = "main" }, boss])
        {
            Parameters = new Dictionary<string, string> { ["r"] = "2" },
            Expressions = new Dictionary<string, string> { ["boss.blend"] = "r * 1.5" },
        };

        var text = CanonicalFormat.Write(doc);

        text.Should().Contain("\"blend\": \"=r * 1.5\",\n      \"body\": \"main\"");
        CanonicalFormat.Write(CanonicalFormat.Read(text)).Should().Be(text);
    }

    [Fact]
    public void Editing_a_parameter_changes_the_geometry_and_keeps_every_face_name()
    {
        var before = Modeler.Regenerate(Plate);
        var edited = DocumentEdits.Apply(Plate, [Set("parameters.width", 120)]);
        var after = Modeler.Regenerate(edited);

        before.Diagnostics.Should().BeEmpty();
        after.Diagnostics.Should().BeEmpty();
        after.Part.Max.X.Should().BeApproximately(120, 1e-9);
        ((Hole)Parametric.Resolve(edited)["hole"]).Center.Should().Equal(30, 30);
        foreach (var source in before.Topology!.Faces.SelectMany(f => f.Sources))
            ReferenceResolver.Resolve(new FaceReference(source, source), after.Topology!).Status
                .Should().Be(ReferenceStatus.Resolved, source);
        (after.Topology!.Faces.Count, after.Topology.Edges.Count).Should().Be((before.Topology.Faces.Count, before.Topology.Edges.Count));
        // The edit changed the stored parameter, not the expressions that follow it.
        edited.Expressions.Should().BeEquivalentTo(Plate.Expressions);
    }

    [Fact]
    public void Edits_set_fields_to_expressions_and_back_and_add_and_remove_parameters()
    {
        var doc = DocumentEdits.Apply(Plate,
        [
            Set("parameters.wall", 3),
            Set("plate.size.2", "=2 * wall"),
            Set("hole.diameter", 6),
        ]);

        doc.Expressions["plate.size.2"].Should().Be("2 * wall");
        doc.Expressions.Should().NotContainKey("hole.diameter");
        ((Block)Parametric.Resolve(doc)["plate"]).Size.Should().Equal(100, 50, 6);
        ((Hole)Parametric.Resolve(doc)["hole"]).Diameter.Should().Be(6);

        var removed = DocumentEdits.Apply(doc, [JsonNode.Parse("""{ "remove": "parameters.wall" }""")!]);
        removed.Parameters.Should().NotContainKey("wall");
        Diagnose(removed).Should().Match<Diagnostic>(d => d.FeatureId == "plate" && d.Message.Contains("unknown parameter 'wall'"));
    }

    [Fact]
    public void Unknown_names_bad_syntax_cycles_and_fractional_counts_are_diagnosed()
    {
        Diagnose(DocumentEdits.Apply(Plate, [Set("hole.diameter", "=bor")]))
            .Should().Match<Diagnostic>(d => d.FeatureId == "hole" && d.Message.Contains("hole.diameter") && d.Message.Contains("unknown parameter 'bor'"));
        Diagnose(DocumentEdits.Apply(Plate, [Set("parameters.depth", "=widht / 2")]))
            .Should().Match<Diagnostic>(d => d.FeatureId == null && d.Message.Contains("parameter 'depth'") && d.Message.Contains("'widht'"));
        Diagnose(DocumentEdits.Apply(Plate, [Set("parameters.width", "=2 * bore")]))
            .Should().Match<Diagnostic>(d => d.Message.Contains("cycle: bore -> depth -> width -> bore"));
        Diagnose(DocumentEdits.Apply(Plate, [Set("boss.origin.1", "=(width")]))
            .Should().Match<Diagnostic>(d => d.FeatureId == "boss" && d.Message.Contains("missing ')'"));
        Diagnose(DocumentEdits.Apply(Plate, [Set("parameters.holes", 2.5)]))
            .Should().Match<Diagnostic>(d => d.FeatureId == "row" && d.Message.Contains("whole number"));
        Diagnose(DocumentEdits.Apply(Plate, [Set("parameters.min", 1)]))
            .Should().Match<Diagnostic>(d => d.Message.Contains("'min' is a function"));
        // Resolved values still go through the ordinary rules.
        Diagnose(DocumentEdits.Apply(Plate, [Set("parameters.width", 8)]))
            .Should().Match<Diagnostic>(d => d.FeatureId == "hole" && d.Message.Contains("diameter must be positive"));
    }

    [Fact]
    public void A_parameter_must_be_a_number_or_an_expression()
    {
        var text = PlateText.Replace("\"holes\": 3", "\"holes\": \"three\"");

        FluentActions.Invoking(() => CanonicalFormat.Read(text)).Should().Throw<FormatException>().WithMessage("*'holes'*'='*");
    }

    static Diagnostic Diagnose(PartDocument doc)
    {
        var result = Modeler.Regenerate(doc);
        result.Topology.Should().BeNull();
        var diagnostic = result.Diagnostics.Should().ContainSingle().Subject;
        diagnostic.Code.Should().Be(Diagnostic.InvalidParameter);
        return diagnostic;
    }

    static JsonNode Set(string path, JsonNode value) => new JsonObject { ["set"] = path, ["value"] = value };

    // A pin whose length is a parameter: one document, several lengths.
    static readonly PartDocument Bolt = CanonicalFormat.Read("""
        {
          "format": "isoform/0",
          "units": "mm",
          "parameters": { "length": 12, "radius": 3 },
          "features": [
            {
              "id": "pin", "type": "revolve", "op": "new",
              "plane": { "origin": [0, 0, 0], "x": [1, 0, 0], "normal": [0, -1, 0] },
              "profile": {
                "start": [0, 0],
                "edges": [
                  { "id": "bottom", "to": ["=radius", 0] },
                  { "id": "shank", "to": ["=radius", "=length"] },
                  { "id": "top", "to": [0, "=length"] },
                  { "id": "axis", "to": [0, 0] }
                ]
              },
              "angle": 360
            }
          ]
        }
        """);

    static AssemblyDocument Bolts(params string?[] lengths) => new(lengths.Select((length, k) =>
        new Component($"b{k}", "bolt.json", new Placement([10 * k, 0, 0], [1, 0, 0], [0, 0, 1]), CanonicalFormat.Hash(Bolt),
            Parameters: length is null ? null : new Dictionary<string, string> { ["length"] = length })).ToList());

    [Fact]
    public void Assembly_variants_regenerate_once_per_resolved_document()
    {
        var text = CanonicalFormat.Write(Bolt);
        // Ten bolts: four at the part's own 12 mm (two of them set it explicitly), six at 2 × 10 mm.
        var assembly = Bolts(null, "12", "20", "2 * 10", "20", null, "20", "20", "24 - 4", "20");

        var result = new AssemblyModeler(new ModelingSettings { CellSize = 0.5 }).Regenerate(assembly, path => path == "bolt.json" ? text : null);

        result.Diagnostics.Should().BeEmpty("the pinned hash is the stored document's, before the overrides");
        result.Instances.Select(i => i.Result).Distinct().Should().HaveCount(2);
        result.Instances.Select(i => i.Hash).Distinct().Should().HaveCount(2);
        result.Instances[0].Result.Should().BeSameAs(result.Instances[1].Result);
        result.Instances[0].Hash.Should().Be(CanonicalFormat.Hash(Parametric.Resolve(Bolt)));
        result.Instances[0].Result.Part.Max.Z.Should().BeApproximately(12, 1e-9);
        result.Instances[2].Result.Part.Max.Z.Should().BeApproximately(20, 1e-9);
        result.Instances[2].Name("pin/shank").Should().Be("b2/pin/shank");
    }

    [Fact]
    public void Overriding_an_unknown_or_bad_parameter_is_diagnosed()
    {
        var text = CanonicalFormat.Write(Bolt);
        var assembly = Bolts("20") with { Components = [.. Bolts("20").Components, Bolts("sqrt(-1)")
            .Components[0] with { Id = "short" }, Bolts("1").Components[0] with { Id = "typo", Parameters = new Dictionary<string, string> { ["lenght"] = "5" } }] };

        var result = new AssemblyModeler(new ModelingSettings { CellSize = 0.5 }).Regenerate(assembly, path => path == "bolt.json" ? text : null);

        result.Instances.Select(i => i.Id).Should().Equal("b0", "short");
        result.Diagnostics.Should().HaveCount(2);
        result.Diagnostics.Should().Contain(d => d.FeatureId == "typo" && d.Code == Diagnostic.InvalidParameter && d.Message.Contains("'lenght'"));
        // A variant that does not resolve is still placed, without geometry, like any invalid part.
        result.Diagnostics.Should().Contain(d => d.FeatureId!.StartsWith("bolt.json:") && d.Code == Diagnostic.InvalidParameter
            && d.Message.Contains("parameter 'length'") && d.Message.Contains("sqrt of a negative"));
        result.Instances[1].Field.Should().BeNull();
    }

    [Fact]
    public void Component_parameters_round_trip_through_the_canonical_format()
    {
        var assembly = Bolts("30", null, "radius * 4");
        var text = CanonicalFormat.Write(assembly);

        text.Should().Contain("\"parameters\": {\n        \"length\": 30\n      }");
        text.Should().Contain("\"length\": \"=radius * 4\"");
        CanonicalFormat.Write(CanonicalFormat.ReadAssembly(text)).Should().Be(text);
        CanonicalFormat.Write(new AssemblyDocument([new Component("a", "a.json", Placement.Identity)])).Should().NotContain("parameters");
    }
}
