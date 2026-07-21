using System.Windows.Media;

namespace AgentDock.Models;

/// <summary>
/// An image attached to a user message, ready to serialize into a Claude Code
/// <c>image</c> content block. <see cref="Base64Data"/> is the raw (already
/// resized/encoded) image bytes base64-encoded; <see cref="MediaType"/> is the
/// MIME type Claude expects (e.g. <c>image/png</c>, <c>image/jpeg</c>).
/// </summary>
public sealed record ImageAttachment(string MediaType, string Base64Data);

/// <summary>
/// UI-side view-model for an image queued in the input area before send. Holds a
/// small frozen <see cref="Thumbnail"/> for the chip / message bubble plus the
/// normalized image bytes that become an <see cref="ImageAttachment"/> on send.
/// Immutable — created by <c>ImageAttachmentHelper</c>.
/// </summary>
public sealed class PendingImageAttachment
{
    public required ImageSource Thumbnail { get; init; }
    public required string DisplayName { get; init; }
    public required string MediaType { get; init; }
    public required byte[] Data { get; init; }

    public ImageAttachment ToAttachment() => new(MediaType, Convert.ToBase64String(Data));
}
