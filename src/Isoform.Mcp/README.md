# Isoform.Mcp

An [MCP](https://modelcontextprotocol.io) server that gives AI agents the Isoform kernel as tools, over stdio. Built on the
official C# SDK ([`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol), Apache-2.0).

Every tool is a pure function: arguments in, one compact JSON object out. Documents are passed as their JSON text or as
a path to a `.json` file. Outputs are summaries (names, counts, measures), never meshes.

| Tool | In | Out |
|---|---|---|
| `describe_format` | – | The document format: every feature type with a JSON example and the face names it makes, naming rules, bodies, edits, assemblies, diagnostic codes |
| `validate` | `document` | `ok`, `hash`, `bodies`, `diagnostics`; rules and compilation only, no meshing |
| `regenerate` | `document`, `cellSize?`, `chordTolerance?`, `density?`, `certifyVolume?` | `ok`, `diagnostics`, face/edge/vertex counts, `box`, per body: face names, volume (and certified bounds), area, centroid, inertia; `ms` |
| `apply_edits` | `document`, `edits[]`, `regenerate?`, `outputPath?` | new `document` and `hash`, `previousHash`, a `result` summary, `references`: `kept`, `appeared`, `gone`, `changed` (e.g. `"plate/z+": "resolved -> split"`) |
| `query` | `document`, `queries[]` of `{kind: ray\|distance\|clearance\|wall, origin?, direction?, point?, body?, a?, b?}` | one answer per query, with face names |
| `export_step` | `document`, `outputPath?`, `name?` | exact STEP AP214 to a file or as text; `unsupported` names the faces without a closed form |
| `assembly` | bundle text or path, or an assembly path (parts next to it); `clearances?`, `within?` | `instances` with solved placements, `mates`, `freeDegrees`, `diagnostics`, `clearances` |

`cellSize` (default 0.5 mm) is the finest meshing cell: features under 2 × cellSize come back as `below-resolution`.

## Example

```json
{"name": "query", "arguments": {"document": "part.json", "queries": [{"kind": "ray", "origin": [50, 25, 20], "direction": [0, 0, -1]}]}}
```

```json
{"hash": "507d…06d5", "answers": [{"kind": "ray", "hit": true, "point": [50, 25, 10], "distance": 10, "face": "plate/z+", "normal": [0, 0, 1]}]}
```

## Register with a client

Claude Code, from the repository root:

```sh
claude mcp add isoform -- dotnet run --project src/Isoform.Mcp
```

`dotnet run` builds first, which slows the first start. For a faster start, publish once and point the client at the
build:

```sh
dotnet publish src/Isoform.Mcp -c Release -o <folder>
claude mcp add isoform -- dotnet <folder>/Isoform.Mcp.dll
```

Other clients take the same command in their config, e.g.
`{"mcpServers": {"isoform": {"command": "dotnet", "args": ["run", "--project", "src/Isoform.Mcp"]}}}`.
Logs go to stderr; stdout carries only the protocol.
