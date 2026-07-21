using AgentDock.Services;
using Xunit;

namespace AgentDock.Tests;

/// <summary>
/// Tests for the deterministic, filesystem-independent string transforms in
/// <see cref="MarkdownHelper"/>: <see cref="MarkdownHelper.PreProcess"/> and
/// <see cref="MarkdownHelper.StripLeadingVersionHeading"/>.
///
/// HOW TO ADD A SNIPPET THAT RENDERS BADLY:
/// If the broken snippet is fixed in the pre-processing stage (bold/italic links,
/// bold spanning a code span or link, version-heading stripping), add an
/// [InlineData(input, expected)] row to the matching [Theory] below. If it's a
/// file-link / path issue, add it to LinkifyPathsTests instead. See README.md.
/// </summary>
public class MarkdownPreProcessTests
{
    // --- Bold/italic wrapped around a whole link: **[t](u)** -> [**t**](u) ---
    // MdXaml parses links before emphasis, so the emphasis delimiters outside a
    // link never pair up. PreProcess moves them inside the label.

    [Theory]
    [InlineData("**[text](https://example.com)**", "[**text**](https://example.com)")]
    [InlineData("a **[link](u)** b", "a [**link**](u) b")]
    [InlineData("- **[item](u)** — trailing", "- [**item**](u) — trailing")]
    public void PreProcess_MovesBoldInsideLinkLabel(string input, string expected)
        => Assert.Equal(expected, MarkdownHelper.PreProcess(input));

    [Theory]
    [InlineData("*[text](https://example.com)*", "[*text*](https://example.com)")]
    [InlineData("a *[link](u)* b", "a [*link*](u) b")]
    public void PreProcess_MovesItalicInsideLinkLabel(string input, string expected)
        => Assert.Equal(expected, MarkdownHelper.PreProcess(input));

    // A genuinely bold link (***t***) is the bold rule's job; make sure the italic
    // rule's negative lookarounds don't also fire on it and corrupt the output.
    [Fact]
    public void PreProcess_BoldLink_NotAlsoTreatedAsItalic()
        => Assert.Equal("[**text**](u)", MarkdownHelper.PreProcess("**[text](u)**"));

    // --- Bold spanning an inline atom (code span / link): split so ** pairs ---
    // MdXaml can't pair **...** when a code span or link sits between the
    // delimiters. PreProcess re-emits bold around each text run instead.

    [Theory]
    [InlineData("**use `git status` now**", "**use** `git status` **now**")]
    [InlineData("**See [foo](u) more**", "**See** [foo](u) **more**")]
    [InlineData("**`code` only**", "`code` **only**")]
    [InlineData("**only `code`**", "**only** `code`")]
    public void PreProcess_SplitsBoldAroundInlineAtoms(string input, string expected)
        => Assert.Equal(expected, MarkdownHelper.PreProcess(input));

    // Plain bold with no inline atom inside must be left exactly as-is.
    [Theory]
    [InlineData("**just bold**")]
    [InlineData("plain text with no markup")]
    [InlineData("`code span alone`")]
    public void PreProcess_LeavesPlainBoldAndCodeUntouched(string input)
        => Assert.Equal(input, MarkdownHelper.PreProcess(input));

    // Regression: two separate bolds, each followed by a code span, on one line. The
    // closing ** of the first bold must NOT pair with the opening ** of the second and
    // wrap the code span between them. MdXaml already renders these correctly on its own,
    // so PreProcess must leave them untouched. Previously the splitter mis-paired the
    // delimiters, producing "**a**:** `x` **and** b**: `y`" — a stray literal **.
    [Theory]
    [InlineData("**iac-aws**: `a` and **gcp**: `b`")]
    [InlineData("**a**: `x`. **b**: `y`")]
    [InlineData("**iac-aws**: `public-api-runs`")]                 // single bold, code outside it
    [InlineData("**one** then `code` then **two**")]
    public void PreProcess_DoesNotMisPairAdjacentBoldsAroundCode(string input)
        => Assert.Equal(input, MarkdownHelper.PreProcess(input));

    // The same hazard, but with a markdown link as the atom between two bolds (the shape
    // LinkifyPaths produces from a bare file path sitting between two bold words).
    [Fact]
    public void PreProcess_DoesNotMisPairAdjacentBoldsAroundLink()
        => Assert.Equal(
            "**foo**: [src/x.cs](u) and **bar**: done",
            MarkdownHelper.PreProcess("**foo**: [src/x.cs](u) and **bar**: done"));

    // --- LooksLikeMarkdown: does a reply need the renderer + source/rendered toggle? ---
    // Regression: a single-line reply carrying inline markdown was shown verbatim (literal
    // ** stars, no formatting) with no toggle, because the old check was text.Contains('\n').

    [Theory]
    // The reported case: bold-wrapped URL + bold word + italic phrase, all on one line.
    [InlineData("**https://keikaku.ai/docs/how-it-runs** — confirmed **live** (HTTP 200). Linked under *Getting started*.")]
    [InlineData("this is **bold** text")]
    [InlineData("this is *italic* text")]
    [InlineData("this is _italic_ text")]
    [InlineData("this is __bold__ text")]
    [InlineData("run `git status` first")]
    [InlineData("see [the docs](https://example.com) for more")]
    [InlineData("visit https://example.com today")]
    [InlineData("line one\nline two")]                       // multi-line always qualifies
    public void LooksLikeMarkdown_TrueForInlineOrMultilineMarkdown(string input)
        => Assert.True(MarkdownHelper.LooksLikeMarkdown(input));

    [Theory]
    [InlineData("just a plain sentence with no markup.")]
    [InlineData("Done.")]
    [InlineData("")]
    [InlineData("2 * 3 = 6 and 4 * 5 = 20")]                 // bare arithmetic, not emphasis
    [InlineData("snake_case_identifier stays plain")]        // underscores inside a word
    public void LooksLikeMarkdown_FalseForPlainSingleLine(string input)
        => Assert.False(MarkdownHelper.LooksLikeMarkdown(input));

    // --- StripLeadingVersionHeading: drop a leading "# vX.Y.Z" + blank line ---

    [Theory]
    [InlineData("# v0.9.0\n\nThe body.", "The body.")]
    [InlineData("# 1.2.3 Some title\n\nThe body.", "The body.")]
    [InlineData("# v0.9.0\nNo blank line.", "No blank line.")]
    public void StripLeadingVersionHeading_RemovesVersionHeading(string input, string expected)
        => Assert.Equal(expected, MarkdownHelper.StripLeadingVersionHeading(input));

    [Theory]
    [InlineData("## v0.9.0\n\nNot an H1.")]                 // only H1 counts
    [InlineData("# Introduction\n\nNot a version.")]        // not version-shaped
    [InlineData("Body first.\n\n# v0.9.0")]                 // not at the start
    public void StripLeadingVersionHeading_LeavesNonVersionHeadings(string input)
        => Assert.Equal(input, MarkdownHelper.StripLeadingVersionHeading(input));
}
