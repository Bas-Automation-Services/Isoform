# The end-to-end demo: a pump stand, designed, built and verified

The proof by demo of `research-plan.md` §15.3, as planned in [`demo-plan.md`](demo-plan.md). A designer's model of a
welded pump stand, cast in one piece with legs that flow into the plates, is turned into what a fabricator makes:
laser-cut plates and sawn tube, each an exact part. The parts leave as one STEP assembly, a DXF per plate, a cut list
and a bill of materials, and the assembly they make is checked against the design in a certified report. Then the
design is edited and the same build document runs again.

## What it shows

- **A free-form design that an implicit kernel builds without failing.** `Examples.PumpStand`: a top whose outline is
  a disc and four lobes blended (k = 20), four Ø50 legs 15.1° off vertical blended (k = 10) into the top and four
  feet, trimmed by plane cuts, with a bore, bolt holes and anchor holes; the pump, the hose and a spanner socket over
  each anchor are keep-out bodies of their own. Two parameters, `height` (600) and `splay` (110).
- **Rationalisation by name.** A build document (`Examples.PumpStandBuild`, `isoform-build/0`) binds every section to
  a design face: the top plate on `cap/face`, the feet on `foot/z+`, the legs along `leg/a` (and its pattern copies).
  Outlines, tube ends, same parts and placements are found, not given.
- **Exact parts.** Each plate is a sketch of lines, arcs and circles extruded; the edges copied from analytic faces
  are exact, the blends are fitted with tangent arcs (8 edges, 48 arcs, largest error 0.026 mm against 0.05). Each
  leg is an extruded annulus cut by two planes: line and ellipse edges in STEP.
- **A report that proves what it can and says what it cannot.** Every check gives `pass` or `fail` only on a certified
  bound or a real point; otherwise `unknown`, with the interval.
- **Edits survive.** The design edited (height 700, splay 140): every name of the build document still binds, the
  top and the feet come out the same documents, the legs longer and leaning more, and `DocumentDiff` names exactly
  that.

## How to run it

**A command.** Builds the stand and writes everything into a folder (default: `isoform-stand` in the system's temp
folder), the design and the build document included:

```sh
dotnet run -c Release --project src/Isoform.Bench -- demo <folder>
```

The same run as a test, with its assertions (below): `dotnet test tests/Isoform.Tests --filter DemoTests`.

**MCP.** Register the server (see `src/Isoform.Mcp/README.md`), then two tools. `build` takes the design, the build
document and a folder; `verify` takes any bundle (or an assembly file with its parts beside it) and checks it against
its design: the design and build document default to the ones it carries.

```json
{"name": "build", "arguments": {"design": "stand.json", "build": "build.json", "outputFolder": "out"}}
{"name": "verify", "arguments": {"bundle": "out/assembly.json"}}
```

```json
{"verdict": "pass", "checks": {"outside": "pass", "uncovered": "reported", "volumes": "reported", "contacts": "pass",
 "keepouts": "pass", "interfaces": "pass", "walls": "reported", "fits": "pass", "diagnostics": "pass"},
 "bundle": "2d82a3b8…", "folder": "…/out", "files": ["assembly.json", "bom.csv", …, "top.json"], "diagnostics": [],
 "summary": "# Verification: PASS\n…", "ms": {"regenerate": 3536, "build": 1469, "outputs": 2750, "verify": 16099}}
```

An agent that starts from a design writes the build document itself: `describe_format` has the format, `regenerate`
the face names to bind.

**Chat.** Run the native server with an API key (`README.md`, Playground), open `stand.json` in the page and ask, for
example, "build this as 12 mm plates on cap/face and foot/z+ and Ø48.3 × 3.2 tube along leg/a, keep out of the pump,
the hose by 5 mm and the sockets". The chat has the same two tools on the page's document (the design); the files go to
a folder on the server's machine, which the answer names. Neither runs in the WebAssembly playground: a build
regenerates the design and searches every part on all cores.

## The outputs

| File | What |
|---|---|
| `assembly.json` | The assembly (`isoform-assembly/0`): nine components, each part pinned by its hash |
| `stand.json`, `build.json` | The design and the build document, as given |
| `top.json`, `feet.json`, `legs.json` | One part document per distinct part, in its own frame |
| `stand.step` | STEP AP214 with assembly structure: three products, nine placed occurrences (OCCT reads it back valid, each part's volume exact: `BuildStepTests`) |
| `top.dxf`, `feet.dxf` | R12 outlines: lines, arcs and circles |
| `cut-list.csv`, `.md` | Per distinct tube: size, quantity, long and short point, end angles, turn between the cuts |
| `bom.csv`, `.md` | Per distinct part: kind, quantity, description, mass each and in all |
| `report.json`, `report.md` | The verification: JSON (`isoform-verification/0`) for agents, markdown for people |

| part | kind | quantity | description | mass each (kg) | mass (kg) |
| --- | --- | --- | --- | --- | --- |
| top | plate | 1 | Plate 12 S235 270 × 270 | 5.74 | 5.74 |
| feet | plate | 4 | Plate 12 S235 160 × 160 | 2.39 | 9.55 |
| legs | tube | 4 | Tube Ø48.3 × 3.2 S235 L 609.7 | 2.12 | 8.49 |
| total |  | 9 |  |  | 23.79 |

| part | quantity | size | material | long point | short point | end A | end B | turn |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| legs | 4 | Ø48.3 × 3.2 | S235 | 609.7 | 583.6 | 15.1° | 15.1° | 180° |

The same inputs give the same files, byte for byte: the bundle, the STEP, the DXFs, the lists and the report (no date
in any of them).

## The report

The head of `report.md` (cell 1 mm, search budget 20,000 boxes):

| Check | Verdict | Result |
|---|---|---|
| Parts outside the design | pass | at most 0.023 mm (top/plate/lobes-3:lobe-blend~12 at design lobes/3:lobe/blend); limit 0.05 mm |
| Design not covered | reported | at most 2.943 mm on leg/blend (legs-1/tube/outside nearest); no limit |
| Volume and mass | reported | parts 23.79 kg, design 52.26 kg; design not covered 3626.6 cm³ |
| Contacts and interference | pass | 8 contacts, 0 interfering, 0 unknown |
| Keep-outs | pass | pump [0.000, 0.000] (≥ 0); hose [10.000, 10.001] (≥ 5); socket-1 [0.000, 0.000] (≥ 0); … |
| Interfaces | pass | 5 of 5 reproduced |
| Minimum wall | reported | thinnest 0.000 mm (legs.json); no limit |
| Fitted edges | pass | 8 fitted edges, largest error 0.0260 mm; limit 0.05 mm |
| Diagnostics | pass | none |

Read it as: every joint is a contact and nothing interferes; the pump and the four sockets touch the parts and do not
enter them; every interface lies on a part face of the same surface at every mesh vertex (worst 0), and the pump seat
and the floor faces are covered, certified; the legs are 0.85 mm inside the Ø50 design legs, settled; the blends no
part covers are what is left of the design (the leg blends about 3 mm). The thinnest wall is the knife edge of a
leg's oblique cut, which the report explains. The top plate's fitted outline stays within 0.023 mm of the design's
blend, the hose is 10 mm clear of the bore, and every hole's wall is covered: the verdict is **pass**, every bound
certified within the budget (the bounds for sketched plates' holes and fitted outlines are in `Queries.Convex`).

The demo test (`tests/Isoform.Tests/Build/DemoTests.cs`) asserts exactly that: the verdict pass, every check pass or
reported; contacts, fits and diagnostics; every keep-out, interface and instance pass; every interface's surface match
(no vertex unmatched, worst ≤ 1e-6); the legs' −0.85 settled.

## The design edited

The edits an agent sends through `apply_edits`: `{"set": "parameters.height", "value": 700}` and
`{"set": "parameters.splay", "value": 140}`. Run again, the design regenerates without diagnostics, the build binds
every name (sections, interfaces, keep-out bodies) and gives the same three parts in nine instances. The top and the
feet are the same documents; the leg is 704.4 mm on its axis, 718.5 at the long point and 690.3 at the short one, both
ends 16.3° off square. Between the two bundles `DocumentDiff` gives the two parameter sets on `stand.json`, five edits
to `legs.json` (its length and end planes), the top's placement 100 mm higher and each leg's new hash and placement,
and nothing else.

## Time

Measured on the desktop at 1 mm: the build tool about 24 s, of which regenerating the design 3.5 s, the plates and
tubes 1.5 s, STEP and the other outputs 3 s and the verification 16 s (assembly meshing, the volumes and the minimum
walls take most of it). The edited design regenerated and built again: 3.6 s. The
demo test (two full builds for the byte comparison, one edited) takes about 90 s.

## What it does not check

- **Welds, fit-up gaps, cutting tolerances.** Joints are contacts between parts as drawn; nothing is welded, gapped
  or listed for welding.
- **Strength and stiffness.** Nothing is sized by calculation; the pump's load is a keep-out, not a load case.
- **Sampled, not certified:** the minimum wall, the mesh-vertex means, and the interface surface match (at mesh
  vertices; their coverage is certified). Volumes and masses are mesh estimates with certified ranges beside them.
- **Fitted edges in `verify`.** Only a build knows its fit errors; `verify` on a hand-made bundle says "not checked".
- **Outside the demo's reach:** a tube that ends on another tube (a cope; refused), a plate pierced by an oblique tube
  (E1), bent tubes (E2), the playground view of the report (P).
