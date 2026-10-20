# Exploratory Draft: AI-Native, Human-Usable CAD Geometry Engine

> **STATUS: DISCUSSION DRAFT — NOT AN APPROVED SPECIFICATION**
>
> This document captures an early concept to discuss with Claude. **Do not treat the architecture, technologies, scope, or timeline below as settled decisions.** The first task is to examine the idea critically, identify uncertainties, and **ask me clarifying questions before implementing anything or making major architectural choices**. Propose alternatives and trade-offs rather than filling gaps with assumptions.

## 1. What I want to explore

I want to investigate building a geometry generation engine from scratch that does **not use B-rep as its authoritative internal solid representation**. The long-term ambition is a browser-based, human-usable parametric mechanical CAD system that is also AI-native.

A human should eventually be able to sketch, dimension, select geometry, create features, and edit models using a familiar CAD interface. An AI agent should be able to perform the same operations through a structured command API. Neither interface should be a second-class citizen.

**Immediate priority: research and prototype the geometry core first.** Do not start by building a complete CAD UI, AI chat experience, backend infrastructure, or application shell.

## 2. Why investigate this?

Traditional B-rep CAD kernels have difficult problems around topology changes, persistent face/edge references, Boolean robustness, tolerances, fillets, and complex generated geometry. A function-based or implicit representation may offer a different set of trade-offs, particularly for programmatic generation and editing.

This is a hypothesis to test, **not** a claim that implicit geometry solves all these problems. We still need accurate dimensions, predictable modeling behavior, efficient evaluation, meaningful selection, and ultimately manufacturing-oriented workflows.

## 3. Tentative direction — open to challenge

One candidate is a **parametric functional representation (F-rep)** with analytic primitives and constructive solid geometry (CSG). A scalar function f(x, y, z) defines inside/outside/on-surface classification; a signed distance field (SDF) is one possible subset, but the engine need not require every node to be an exact distance function.

Potential building blocks:

- Analytic primitives such as boxes, spheres, cylinders, and planes.
- Transformations, unions, intersections, and differences.
- A symbolic, parameterized dependency graph rather than storing sampled voxels as the primary model.
- Explicit bounds and numerical evaluation policies.
- Semantic feature identities and provenance, independent of transient tessellation IDs.
- A meshing/export layer separate from the source representation.

**Questions to investigate:** Is pure F-rep suitable for precise mechanical CAD? Would a hybrid of implicit fields, exact analytic geometry, and procedural feature definitions be better? What are the failure modes? What can we learn from nTop, libfive, OpenVDB, and related approaches? Should the kernel be written from scratch or built on an existing library?

## 4. The first technical proof of concept

The first experiment should be deliberately small and falsifiable:

1. Define a parameterized 2D rectangular profile, possibly with a circular cutout.
2. Produce an extrusion as an implicit/function-based solid.
3. Evaluate its field and bounds, and generate a usable mesh for inspection.
4. Change dimensions and regenerate deterministically.
5. Identify semantic regions such as the extrusion's positive/negative end caps, side regions, and hole wall **without relying on mesh triangle IDs or persistent B-rep faces**.
6. Test whether those references survive parameter edits, and explicitly detect cases where they cannot.
7. Check numerical accuracy, watertightness, performance, and difficult edge cases.

The most important research question is **whether we can preserve CAD-like geometric meaning and stable references without recreating a conventional B-rep topology system**.

### Possible acceptance tests (to refine together)

- Parameter changes produce predictable results without stale references.
- The resulting surface can be sampled and meshed within an explicitly defined tolerance.
- A semantic query can identify a cap or hole wall after dimensions change.
- Degenerate and invalid parameter combinations return understandable diagnostics.
- Geometry is deterministic and testable without a graphical application.

**Do not invent numerical tolerance targets, performance budgets, or supported geometry classes without discussing them with me.**

## 5. Possible future architecture (NOT MVP commitments)

A possible longer-term structure:

```text
Human CAD UI ──────────┐
                       ├── Shared typed command/query API
AI agent / automation ─┘               │
                                       ▼
                           Parametric document graph
                                       │
                                       ▼
                          Implicit / hybrid geometry core
                                       │
                         ┌─────────────┴─────────────┐
                         ▼                           ▼
                   Visualization               Validation/export
```

Future capabilities might include:

- Browser-based sketching with dimensions and constraints.
- Extrude, cut, holes, patterns, fillets, and eventually assemblies.
- Semantic selection through ray hits plus geometric/feature queries.
- Human and AI operations sharing the same typed commands, undo/redo, transactions, and validation.
- Multiple rendering resolutions, and eventual manufacturing-oriented exports.

These are **long-term ideas, not instructions to build them now**.

## 6. Technology ideas — not fixed

I have experience with C#/.NET, React, TypeScript, and CAD automation. A .NET geometry core with independent tests could be practical. A future browser UI could use React and Three.js or another renderer. Other languages, libraries, architectures, and approaches are welcome if justified.

Do **not** assume .NET, a particular solver, GPU framework, server architecture, database, or deployment model is mandatory. First consider the constraints and trade-offs.

## 7. Research risks to discuss

- Difference between an implicit sign function and a true signed distance function.
- Numerical tolerances, gradients, discontinuities, and sharp features.
- Reliable Boolean classification and tangential/degenerate contacts.
- Feature identity and semantic selection when regions split, merge, or disappear.
- Exact dimensions and manufacturability versus approximate mesh extraction.
- Performance and caching for large symbolic graphs.
- Sketch constraint solving and mapping 2D profiles to 3D fields.
- How and whether to export precise geometry to STEP or other CAD formats later.
- How the approach compares to existing open-source kernels and commercial implicit CAD systems.

## 8. How I want Claude to work with me

**Your first response should be a discussion, not code.**

1. Briefly restate what you think I am trying to achieve.
2. Critically evaluate the idea and call out any misconceptions or weak assumptions.
3. Identify the most consequential unresolved decisions and ask me **a focused, prioritized set of questions**. Explain why the answers matter. Do not overwhelm me with dozens of low-level implementation questions at once.
4. Offer 2–3 plausible approaches where appropriate, including their trade-offs, rather than prematurely selecting one.
5. Distinguish **agreed decisions**, **working hypotheses**, and **open questions** as our discussion progresses.
6. After we align, propose a minimal research spike with clear tests and stop/go criteria. **Ask for my approval before generating a repository or implementing the spike.**
7. If a detail is ambiguous, **ask rather than silently assuming**. For minor reversible choices, you may suggest a default, but label it as a proposal.
8. Do not interpret the sample architecture, tech stack, milestones, or feature list as requirements already approved by me.

### Suggested first questions to consider

- What level of geometric precision and manufacturing usefulness do I ultimately expect?
- Should the initial core target only prismatic mechanical parts, or arbitrary freeform geometry too?
- How much interoperability with existing CAD systems is important, and when?
- Is avoiding B-rep an absolute requirement, or is a hybrid representation acceptable if B-rep is only used at boundaries?
- What would convince us that this representation is promising enough to continue?

## 9. Desired outcome of our initial conversation

A jointly agreed **research plan for the geometry core**, with explicit hypotheses, a small prototype scope, measurable validation criteria, known limitations, and a list of decisions still open.

Again: **this is an exploratory draft, not a build order. Ask me questions and challenge the design before coding.**
