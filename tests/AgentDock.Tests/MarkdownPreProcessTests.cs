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
