using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentDock.Models;

namespace AgentDock.Services.Abstractions;

/// <summary>Checks GitHub releases for a newer build and installs it.</summary>
public interface IUpdateCheckService
{
    Task<UpdateInfo?> CheckForUpdateAsync(UpdateChannel channel = UpdateChannel.Stable);

    Task<string?> DownloadInstallerAsync(
        string downloadUrl,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    void LaunchUpdateAndShutdown(string installerPath);
}

/// <summary>Reads the embedded per-version release notes.</summary>
public interface IReleaseNotesService
{
    string? GetNotesForVersion(string version);
}

/// <summary>Builds pending image attachments from files, the clipboard, or a bitmap.</summary>
public interface IImageAttachmentService
{
    bool IsSupportedImageFile(string path);
    ImageSource CreatePreview(PendingImageAttachment attachment);
    PendingImageAttachment? FromFile(string path);
    PendingImageAttachment? FromClipboard();
    PendingImageAttachment? FromBitmap(BitmapSource source, string displayName);
}

/// <summary>
/// Renders markdown into WPF documents and wires their interactive behaviour.
///
/// The dependency-free text transforms (<c>PreProcess</c>, <c>LooksLikeMarkdown</c>,
/// <c>StripLeadingVersionHeading</c>, <c>LinkifyPaths</c>) remain static on the
/// implementation: they are pure functions with nothing to substitute, and are covered
/// directly by unit tests.
/// </summary>
public interface IMarkdownRenderer
{
    void ApplyCodeBlockTheme(System.Windows.Documents.FlowDocument? doc);
    void RenderTo(MdXaml.MarkdownScrollViewer viewer, string markdown);

    System.Windows.Documents.FlowDocument BuildDocument(
        string markdown,
        System.Windows.Style? markdownStyle,
        string projectPath,
        Action<string>? onFileLinkClicked);

    System.Windows.Documents.FlowDocument BuildPlainTextFallback(
        string text,
        System.Windows.Style? markdownStyle);

    void ConvertTablesToGrids(System.Windows.Documents.FlowDocument? doc);
    void WireLinks(System.Windows.Documents.FlowDocument? doc, Action<string>? onFileClick);
    void EnableMarkdownLinkCopy(System.Windows.DependencyObject? host);
}
