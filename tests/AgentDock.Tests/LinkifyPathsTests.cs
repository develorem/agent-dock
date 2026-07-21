using System.IO;
using AgentDock.Services;
using Xunit;

namespace AgentDock.Tests;

/// <summary>
/// Tests for <see cref="MarkdownHelper.LinkifyPaths"/> — the stage that turns bare
/// URLs and project-relative file paths into clickable links before MdXaml parses.
///
/// Path resolution requires the file to exist on disk (File.Exists), so each test
/// runs against a throwaway temp project tree created in the constructor. Because
/// the emitted file URL embeds an escaped absolute path, assertions check the link
/// label and the agentdock-file:/// prefix rather than the full URL string.
///
/// HOW TO ADD A SNIPPET: if the broken snippet involves a path/URL not linkifying
/// (or linkifying wrong — bad label escaping, leaked backticks, linkified inside a
/// code fence), create the referenced file in the constructor and add a test here.
/// </summary>
public class LinkifyPathsTests : IDisposable
{
    private readonly string _root;

    public LinkifyPathsTests()
    {
        // Unique temp project root. (Math.Random/DateTime.Now are unavailable in some
        // hosts; Path.GetRandomFileName is deterministic-enough and collision-safe.)
        _root = Path.Combine(Path.GetTempPath(), "agentdock-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        // A file whose name contains an emphasis character (_) — the regression case
        // for label escaping: an unescaped underscore italicizes "permission..groups".
        File.WriteAllText(Path.Combine(_root, "docs", "permission_groups.cs"), "// test");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // --- Bare URLs become real links ---

    [Fact]
    public void Linkify_WrapsBareUrl()
        => Assert.Equal(
            "See [https://example.com](https://example.com) here",
            MarkdownHelper.LinkifyPaths("See https://example.com here", _root));

    // A bold-wrapped bare URL: the match must STOP at the closing ** rather than
    // swallowing the asterisks into the URL. Otherwise PreProcess can't pair the
    // bold and the leading ** renders as literal stars.
    [Fact]
    public void Linkify_BoldWrappedUrl_StopsAtClosingDelimiters()
        => Assert.Equal(
            "**[https://resend.com](https://resend.com)** — sign up",
            MarkdownHelper.LinkifyPaths("**https://resend.com** — sign up", _root));

    [Fact]
    public void Linkify_LeavesExistingMarkdownLinkAlone()
    {
        const string input = "[click](https://example.com)";
        Assert.Equal(input, MarkdownHelper.LinkifyPaths(input, _root));
    }

    // --- Resolvable file paths become agentdock-file links with an escaped label ---

    [Fact]
    public void Linkify_WrapsExistingRelativePath()
    {
        var output = MarkdownHelper.LinkifyPaths("Edit docs/permission_groups.cs to fix", _root);
        // Label keeps the path text but escapes the underscore so it doesn't italicize.
        Assert.Contains(@"[docs/permission\_groups.cs](agentdock-file:///", output);
        // The unescaped form must NOT appear — that's the bug we're guarding against.
        Assert.DoesNotContain("permission_groups.cs](", output);
    }

    [Fact]
    public void Linkify_BacktickPathBecomesPlainLink_NoBackticks()
    {
        // MdXaml parses code spans before anchors, so a `code` span inside a link
        // label breaks the link. LinkifyPaths must drop the backticks for file refs.
        var output = MarkdownHelper.LinkifyPaths("Open `docs/permission_groups.cs` now", _root);
        Assert.Contains(@"[docs/permission\_groups.cs](agentdock-file:///", output);
        Assert.DoesNotContain("`", output);
    }

    [Fact]
    public void Linkify_NonExistentPath_LeftAsIs()
    {
        const string input = "Edit missing/nope.cs please";
        Assert.Equal(input, MarkdownHelper.LinkifyPaths(input, _root));
    }

    [Fact]
    public void Linkify_NonPathBacktickSpan_KeepsBackticks()
    {
        // `git status` has no slash and resolves to no file: it must stay a code span.
        const string input = "Run `git status` now";
        Assert.Equal(input, MarkdownHelper.LinkifyPaths(input, _root));
    }

    [Fact]
    public void Linkify_PathInsideFencedCodeBlock_NotLinkified()
    {
        const string input = "```\ndocs/permission_groups.cs\n```";
        var output = MarkdownHelper.LinkifyPaths(input, _root);
        Assert.DoesNotContain("agentdock-file", output);
        Assert.Contains("docs/permission_groups.cs", output);
    }

    // --- The combined pipeline, in the same order BuildDocument runs it ---

    [Fact]
    public void Pipeline_LinkifyThenPreProcess_HandlesPathAndBoldLink()
    {
        var pipeline = MarkdownHelper.PreProcess(
            MarkdownHelper.LinkifyPaths("**[docs](https://x.com)** and docs/permission_groups.cs", _root));
        Assert.Contains("[**docs**](https://x.com)", pipeline);                       // bold moved inside label
        Assert.Contains(@"[docs/permission\_groups.cs](agentdock-file:///", pipeline); // path linkified + escaped
    }

    // Regression: a bare URL wrapped in bold must end up as a bold hyperlink, not a
    // stray ** + broken link. Linkify stops the URL at the ** (see UrlCandidateRegex),
    // then PreProcess's BoldLinkRegex moves the bold inside the label.
    [Fact]
    public void Pipeline_BoldWrappedUrl_BecomesBoldLink()
    {
        var pipeline = MarkdownHelper.PreProcess(
            MarkdownHelper.LinkifyPaths("**https://resend.com** — sign up there", _root));
        Assert.Equal("[**https://resend.com**](https://resend.com) — sign up there", pipeline);
    }
}
