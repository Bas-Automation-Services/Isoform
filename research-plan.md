# Isoform: Geometry Core Research Plan

> Status: spike run (2026-10-20); results in section 11. Builds on `cad_geometry_engine_draft.md`.

## 1. Goal

Ship usable, manufacturing-grade mechanical CAD that humans and AI agents drive through the same
typed command API. This spike answers one question first:

**Does an implicit (F-rep/CSG) core give stable, CAD-meaningful references — and anything else B-rep
does not — while still producing exact output (STEP, drawings)?**

If not, the fallback is a B-rep (OCCT) product with the reference and versioning layer from this
spike on top. That layer is designed to survive either outcome.

## 2. Decisions

### Agreed

| Topic | Decision |
|---|---|
| Purpose | Ship usable CAD, not a research result |
| Exactness | STEP and exact drawings are required → a B-rep must exist, at least as a derived export |
| References | Faces, edges and vertices |
| Fillets | True constant-radius fillets are core |
| Approach | Verify the idea first; B-rep comparison is the stop/go test |
| Stack | C# / .NET 10 |
| Licence | MPL-2.0, open source |
| Units | mm |
| Modeling tolerance | `1e-7` mm (matches OCCT `Precision::Confusion`), configurable |
| Mesh tolerance | Separate chord tolerance, configurable; tested for convergence, not held to the modeling tolerance |
| Coplanar / split faces | Every face keeps a **source** identity and a **result** identity (section 4) |
| Versioning | Git-style: version the feature graph, never the geometry (section 5) |
| Versioning backend | Canonical text format that works with real git now and can back an in-app history later |

### Working hypotheses

- **H1 – References.** References that bind to the feature graph (source identities) resolve correctly after
  every edit in the suite, or report *split / gone / ambiguous* explicitly. They never silently rebind.
- **H2 – Advantage over B-rep.** On the same suite, the F-rep scheme beats an OCCT baseline that uses OCCT's
  operation history (`Modified` / `Generated` / `IsDeleted`), at least on the split and vanish cases.
- **H3 – Exact output.** From the CSG tree we get (a) a mesh whose measured error against the analytic
  surface converges below the chord tolerance, with sharp edges preserved, and (b) an OCCT B-rep rebuilt
  from the same tree, exported to STEP, whose faces map 1:1 back to the references.
- **H4 – Fillets.** Constant-radius fillets on plane–plane edges (90° and non-90°) and on a plane–cylinder edge
  (torus) deviate from the true rolling-ball surface by ≤ the modeling tolerance.
- **H5 – Determinism.** The same document gives the same content hash, the same result and the same reference
  IDs across runs (and across machines with the same pinned OCCT build).

## 3. Test part and edit suite

The base part is a plate (W × L × T), a hole (Ø D at x, y), an optional slot through one side, an optional
boss on top, and a fillet on one edge.

Before each edit, faces, edges and vertices are referenced, and each case has an expected outcome:

| # | Edit | Expected |
|---|---|---|
| E1 | Change W / L / T within valid values | Everything resolves |
| E2 | Move the hole inside the plate | Resolves |
| E3 | Hole touches a side face | Degenerate-contact diagnostic |
| E4 | Hole crosses a side | Side face: one source, two result pieces; hole wall partial |
| E5 | Hole fully off the plate | Hole wall *gone* |
| E6 | D > W | Body splits into two solids → diagnostic |
| E7 | Slot through a side | Side face: one source, two result pieces |
| E8 | Boss flush with top face | One result face, two sources |
| E9 | T ≤ 0 | Invalid-parameter diagnostic |
| E10 | Fillet radius larger than the neighbouring face | Fillet-invalid diagnostic |

The cases are stored as data files and run unchanged against both the F-rep core and the OCCT baseline.

## 4. Reference model

- **Source identity:** feature ID + primitive surface (e.g. `boss/top`, `hole/wall`). Stable across edits.
- **Result identity:** a connected piece that exists after regeneration. One result face can have several
  sources (merge); one source can produce several result pieces (split).
- **Edges** = pairs of faces, **vertices** = triples, both at source and result level.
- A reference binds to either level. A feature that consumes a source reference works on **all** its result
  pieces. Only a reference to one specific result piece can become *ambiguous*. That is reported, never guessed.

## 5. Versioning and determinism

- The document (feature graph + parameters) is the only thing stored. Geometry is regenerated, and caches
  are keyed by content hash.
- Canonical text format: fixed ordering, stable feature IDs (never list-index based), doubles written so they
  read back exactly. Same document → same bytes → same hash.
- Later, not in the spike: commits, branches, feature-level diff, and three-way merge. Merge has
  textual conflicts (same parameter) and semantic conflicts (a reference becomes *gone* / *ambiguous*).

## 6. OCCT baseline route

- A small C++ shim `native/Isoform.Occt`, written fresh for this repo, exporting only what the spike needs:
  box, cylinder, cut / fuse / common, fillet, **operation history**, face/edge queries, STEP write.
  C ABI: opaque handles, int return codes, a last-error string.
- Built with CMake against a local OCCT build, found through `OpenCASCADE_DIR` / `OCCT_ROOT`.
  No machine paths in the repo.
- Consumed from C# through `LibraryImport`. Optional: the core builds and tests without OCCT;
  OCCT tests skip when the native library is absent.
- The spike pins OCCT 8.0.0-rc2. Results only match across machines with the same OCCT build.
  Before shipping, move from the release candidate to the final 8.0.0.

## 7. Proposed layout

Solution layout: projects at top level, a `tests` folder, `Solution Items`.

```text
Isoform.sln
src/Isoform.Document      feature graph, canonical format, content hash, edits as data
src/Isoform.Geometry      primitives, CSG, field + ownership evaluation
src/Isoform.Meshing       feature-preserving dual contouring
src/Isoform.Modeling      regeneration, topology, references, diagnostics (backend-neutral API)
src/Isoform.Occt          OCCT baseline modeler, LibraryImport shim to native/Isoform.Occt
native/Isoform.Occt       C++ C-ABI shim over OCCT 8.0.0-rc2
tests/Isoform.Tests       xUnit + FluentAssertions 6.12; edit suite in Cases/edit-suite.json
```

Point/vector types come from the public `BasAutomation.Geometry` NuGet package (no submodule).
Its `==` is tolerance-based, so its types are never used as dictionary keys in the core.

## 8. Out of scope

Sketch solver, UI, performance beyond basic timings, patterns, assemblies, server/Linux builds, branch/merge.

## 8b. Open source

Isoform will be open source under **MPL-2.0** (`LICENSE`): changes to Isoform's own files stay open,
and closed-source applications can still be built on top. Consequences for the spike:

- No machine paths, private repo names or copied in-house code in the repo.
- Dependencies must be licence-compatible: OCCT (LGPL-2.1 + exception, dynamically linked),
  BasAutomation.Geometry (MIT), xUnit (Apache-2.0), FluentAssertions 6.12 (Apache-2.0; v8+ is not free
  for commercial use, so it stays on 6.x).
- The core must build and test without OCCT installed.


## 9. Stop / go

| Result | Meaning |
|---|---|
| H1–H5 all pass, H2 shows a clear advantage | **Go**: F-rep core, B-rep derived for export |
| H2 equal or worse | **Pivot**: build on OCCT B-rep; keep the reference + versioning layer |
| H4 fails for non-90° or plane–cylinder fillets | **Hybrid required**: fillets come from B-rep |
| H3b mapping not 1:1 | **Rethink export**: references would not survive into STEP |

## 10. Still open

- Direction after the spike (section 11): build the product on OCCT, or keep F-rep in a supporting role.

## 11. Results (2026-10-20)

Default settings: tolerance 1e-7 mm, cell size 0.5 mm. Core: 27 tests, OCCT baseline: 19 tests, all green.

| | Result | Evidence |
|---|---|---|
| H1 – References | **Pass.** All 11 edit cases resolve as expected, including split, merge, gone and ambiguous piece references. The base part's derived topology is exactly a B-rep's: 8 faces, 17 edges, 10 vertices. | `EditSuiteTests`, `FrepTopologyTests` |
| H2 – Advantage over B-rep | **Fail: no naming advantage.** On OCCT with history-based naming plus the same source/signature layer, every reference resolves identically (0 mismatches on either backend). Topology is identical in 10 of 11 cases. The only difference is E03 (hole tangent to a side): F-rep flags the zero-thickness contact; OCCT builds a pinched solid, splits the hole wall in two, reports it valid, and gives no warning. | `BackendComparisonTests`, `reference-comparison.md` in its output |
| H3a – Mesh | **Pass.** Watertight, outward, volume within 7e-5 of exact. Vertices lie on the exact surface to ~1e-15 mm. Chord error converges quadratically: 2.0e-2 / 5.4e-3 / 1.3e-3 mm at 0.5 / 0.25 / 0.125 mm cells, so 0.01 mm needs cells of about 0.35 mm around R2 features. | `MeshQualityTests` |
| H3b – Exact output | **Pass, via OCCT.** The same document rebuilt in OCCT exports to STEP, and its faces map 1:1 to the F-rep faces by sources and neighbour signatures. The exact output is a B-rep, so the B-rep has to be built anyway. | `BackendComparisonTests` |
| H4 – Fillets | **Pass for the supported pairs.** Fillet vertices lie within 7e-15 mm of the analytic rolling-ball surface at 90°, 60° and 120°, and on the hole-edge torus. OCCT's own fillet surfaces are the same cylinders and torus to 1e-9. Limits: exact only for plane–plane and plane ⊥ cylinder; only convex edges inside one intersection. | `FilletTests`, `FilletCrossCheckTests` |
| H5 – Determinism | **Pass.** Golden content hash; regeneration is bit-for-bit identical. | `DocumentTests`, `MeshQualityTests` |

### What this means

- **Naming robustness comes from the reference layer, not the representation.** Binding references to
  sources (feature + primitive surface) plus neighbour signatures, with explicit split / merged / gone / ambiguous,
  works the same on both backends. F-rep's ownership makes the sources cheap to compute; OCCT's history gives them too.
- **Exact output requires a B-rep anyway.** STEP came from OCCT. In a pure F-rep product the B-rep becomes a
  second, always-required representation, and keeping it consistent is the hard part.
- **Fillets are F-rep's weak spot beyond the simple cases.** Exactness here relied on closed-form pairs; general
  surface pairs, concave edges, variable radius and corner blends are routine for OCCT and open research for F-rep.
- **Where F-rep did better:** cheap point classification caught the degenerate tangent case that OCCT built
  silently. Analytic checks like that can also run in front of a B-rep kernel.

### Stop / go

By section 9, H2 equal means **pivot: build on OCCT B-rep and keep the reference + versioning layer.**
Everything backend-neutral carries over unchanged: `Isoform.Document` (canonical format, hash, edits),
the `Isoform.Modeling` API (topology, references, diagnostics), the edit suite, and the comparison harness.
