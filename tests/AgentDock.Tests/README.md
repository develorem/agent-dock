# AgentDock.Tests

Unit tests for Agent Dock. The focus right now is the **markdown string-transform
layer** — the pure `string -> string` stage of `MarkdownHelper` that runs before
MdXaml parses. Almost every markdown rendering bug we've hit was fixed here, so
this is where regressions are cheapest to catch.

## Running

```powershell
dotnet test tests/AgentDock.Tests/AgentDock.Tests.csproj
```

(or `dotnet test AgentDock.sln` from the repo root).

## What's covered

| File | Method under test | Catches |
|------|-------------------|---------|
| `MarkdownPreProcessTests.cs` | `PreProcess`, `StripLeadingVersionHeading` | bold/italic wrapped around a link, bold spanning a code span or link, version-heading stripping |
| `LinkifyPathsTests.cs` | `LinkifyPaths` (+ the combined `PreProcess(LinkifyPaths(...))` pipeline) | bare URLs, project file paths, label escaping (`permission_groups`), leaked backticks, code-fence exclusion |

These tests assert on the **preprocessed markdown string**, not the rendered
`FlowDocument`. That's deliberate (scope chosen: string-transform only). They do
not exercise the WPF render stage (`BuildDocument`, `ConvertTablesToGrids`,
`WireLinks`, code-block theming) — those need an STA thread and aren't covered yet.

## How to capture a snippet that renders badly

When a markdown snippet renders wrong:

1. **Find which stage owns the fix.** Run the snippet through `PreProcess` (and
   `LinkifyPaths` if it involves a path/URL) and look at the output string.
   - Output already wrong → it's a string-transform bug → add a test here.
   - Output looks right but it still renders wrong → the bug is in the WPF render
     stage (not yet covered by this project); note it for a future STA test pass.
2. **Add the case:**
   - Pre-processing (bold/italic/links/version heading) → add an `[InlineData(...)]`
     row to the matching `[Theory]` in `MarkdownPreProcessTests.cs`.
   - Path/URL linkifying → add a `[Fact]`/`[Theory]` to `LinkifyPathsTests.cs`. If
     the snippet references a file, create it in the test constructor's temp tree.
3. Run the test red, apply the fix in `MarkdownHelper`, run it green.

`tests/testdata/markdown-rendering-test.md` is a separate manual/visual reference
sheet for eyeballing the full render — not automated.
