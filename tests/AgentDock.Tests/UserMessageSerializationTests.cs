using System.Text.Json;
using AgentDock.Models;
using AgentDock.Services;
using Xunit;

namespace AgentDock.Tests;

/// <summary>
/// Locks the stdin JSON shape produced for user messages — the contract Claude
/// Code's stream-json input parses. Text-only messages must stay a plain string
/// (the long-standing shape); image messages must become an Anthropic
/// content-block array with base64 image sources.
/// </summary>
public class UserMessageSerializationTests
{
    private static JsonElement ParseContent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("message").GetProperty("content").Clone();
    }

    [Fact]
    public void TextOnly_SerializesContentAsPlainString()
    {
        var json = ClaudeSession.SerializeUserMessage("hello world", null);
        var content = ParseContent(json);

        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Equal("hello world", content.GetString());
    }

    [Fact]
    public void EmptyImageList_SerializesContentAsPlainString()
    {
        var json = ClaudeSession.SerializeUserMessage("hi", new List<ImageAttachment>());
        var content = ParseContent(json);

        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Equal("hi", content.GetString());
    }

    [Fact]
    public void WithImage_SerializesImageBlockThenTextBlock()
    {
        var images = new List<ImageAttachment> { new("image/png", "QUJD") };
        var json = ClaudeSession.SerializeUserMessage("what is this?", images);
        var content = ParseContent(json);

        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(2, content.GetArrayLength());

        // Images come first.
        var image = content[0];
        Assert.Equal("image", image.GetProperty("type").GetString());
        var source = image.GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.Equal("QUJD", source.GetProperty("data").GetString());

        // Then the text block.
        var text = content[1];
        Assert.Equal("text", text.GetProperty("type").GetString());
        Assert.Equal("what is this?", text.GetProperty("text").GetString());
    }

    [Fact]
    public void ImageWithoutText_OmitsTextBlock()
    {
        var images = new List<ImageAttachment> { new("image/jpeg", "Rk9P") };
        var json = ClaudeSession.SerializeUserMessage("   ", images);
        var content = ParseContent(json);

        // Whitespace-only text is sent by the UI as-is here; SerializeUserMessage
        // only drops genuinely empty text. The control trims before calling, so an
        // image-only send arrives as "".
        var jsonEmpty = ClaudeSession.SerializeUserMessage("", images);
        var contentEmpty = ParseContent(jsonEmpty);

        Assert.Equal(JsonValueKind.Array, contentEmpty.ValueKind);
        Assert.Equal(1, contentEmpty.GetArrayLength());
        Assert.Equal("image", contentEmpty[0].GetProperty("type").GetString());

        // Sanity: the whitespace variant still includes a text block (not empty).
        Assert.Equal(2, content.GetArrayLength());
    }

    [Fact]
    public void MultipleImages_AllSerializeBeforeText()
    {
        var images = new List<ImageAttachment>
        {
            new("image/png", "AAAA"),
            new("image/jpeg", "BBBB"),
        };
        var json = ClaudeSession.SerializeUserMessage("compare these", images);
        var content = ParseContent(json);

        Assert.Equal(3, content.GetArrayLength());
        Assert.Equal("image", content[0].GetProperty("type").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("text", content[2].GetProperty("type").GetString());
    }
}
