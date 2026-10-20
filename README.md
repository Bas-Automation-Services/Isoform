# Isoform

Research spike for an AI-native, human-usable parametric CAD geometry core.

The question: can an implicit (F-rep/CSG) core keep stable, CAD-meaningful references to faces, edges and
vertices while producing exact output, and does it do better at this than a B-rep kernel? See
[`research-plan.md`](research-plan.md) for the hypotheses, the edit suite and the results.

## Layout

| Project | What |
|---|---|
| `Isoform.Document` | Feature graph, canonical text format (git-diffable), content hash, edits as data |
| `Isoform.Geometry` | Analytic surfaces, CSG fields with ownership, exact rolling-ball fillets |
| `Isoform.Meshing` | Feature-preserving dual contouring |
| `Isoform.Modeling` | Regeneration, topology, references, diagnostics: the backend-neutral API |
| `Isoform.Occt` | OCCT B-rep baseline behind the same API |
| `native/Isoform.Occt` | Small C ABI over OpenCASCADE used by `Isoform.Occt` |

## Build and test

Requires the .NET 10 SDK.

```sh
dotnet test tests/Isoform.Tests        # core: needs nothing else
```

The OCCT baseline is optional. It needs an OpenCASCADE 8.0 build, CMake ≥ 3.21 and a C++17 compiler:

```sh
# installed OCCT (folder with OpenCASCADEConfig.cmake)
cmake -S native/Isoform.Occt -B native/build -DOpenCASCADE_DIR=<occt-install>/cmake
# or a plain OCCT build folder (inc/ and win64/vc14/{lib,bin})
cmake -S native/Isoform.Occt -B native/build -DOCCT_ROOT=<occt-build>

cmake --build native/build --config Release
dotnet test tests/Isoform.Occt.Tests
```

## Licence

[MPL-2.0](LICENSE). OpenCASCADE is LGPL-2.1 with an exception and is linked dynamically.
