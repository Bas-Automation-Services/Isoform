# Isoform

Research spike for an AI-native, human-usable parametric CAD geometry core.

The question: can an implicit (F-rep/CSG) core keep stable, CAD-meaningful references to faces, edges and
vertices while producing exact output, and does it do better at this than a B-rep kernel? See
[`research-plan.md`](research-plan.md) for the hypotheses, the edit suite and the results.

## Layout

| Project | What |
|---|---|
| `Isoform.Document` | Feature graph, canonical text format (git-diffable), content hash, edits as data |
| `Isoform.Geometry` | Analytic surfaces (plane, cylinder, sphere, cone, torus), CSG fields with ownership, swept profiles, exact rolling-ball fillets, smooth blends |
| `Isoform.Meshing` | Adaptive, feature-preserving dual contouring with a resolution guarantee |
| `Isoform.Modeling` | Regeneration, topology, references, diagnostics: the backend-neutral API |
| `Isoform.Occt` | OCCT B-rep baseline behind the same API |
| `Isoform.Playground` | The kernel in the browser (WebAssembly): document editor and 3D view |
| `native/Isoform.Occt` | Small C ABI over OpenCASCADE used by `Isoform.Occt` |

## Features

Block, hole, plane cut; extrude and revolve of line-and-arc sketches; linear, circular and mirror patterns;
fillet and chamfer (also on faces shared with earlier fillets, on swept edges and after a shell); shell; offset;
smooth join and cut. Every face is named after the feature that made it, and keeps that name through edits.

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

## Playground

The kernel compiled to WebAssembly, running in the browser: edit the document, see the part regenerate, hover a
face for its name, click two faces to get the edge to fillet, read the diagnostics.

```sh
dotnet run --project src/Isoform.Playground     # http://localhost:5240, interpreted: fine for small parts
```

For a faster build, compiled ahead of time (needs `dotnet workload install wasm-tools` once):

```sh
dotnet publish src/Isoform.Playground -c Release -o out/playground
python -m http.server 8137 -d out/playground/wwwroot   # or any static file server
```

The page needs no network: three.js r160 is vendored in `wwwroot/vendor/three`. In the browser the kernel runs on one thread, so it is several times slower
than on the desktop.

## Licence

[MPL-2.0](LICENSE). OpenCASCADE is LGPL-2.1 with an exception and is linked dynamically. The vendored three.js is
MIT ([its licence](src/Isoform.Playground/wwwroot/vendor/three/LICENSE)).
