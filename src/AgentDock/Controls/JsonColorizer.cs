using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace AgentDock.Controls;

/// <summary>
/// Lightweight JSON syntax colorizer. Distinguishes object keys from string values,
/// numbers, booleans, null, and punctuation using theme-aware brushes.
/// </summary>
public partial class JsonColorizer : DocumentColorizingTransformer
{
    // Strings (with escapes), numbers, keywords, punctuation — single pass, ordered so
    // strings are matched first so their content isn't tokenized as keywords.
    [GeneratedRegex(
        @"(?<str>""(?:\\.|[^""\\])*"")|(?<kw>\b(?:true|false|null)\b)|(?<num>-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)|(?<punct>[\{\}\[\],:])",
        RegexOptions.Compiled)]
    private static partial Regex Tokens();

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length == 0)
            return;

        var keyBrush = GetBrush("JsonKeyForeground");
        var stringBrush = GetBrush("JsonStringForeground");
        var numberBrush = GetBrush("JsonNumberForeground");
        var booleanBrush = GetBrush("JsonBooleanForeground");
        var nullBrush = GetBrush("JsonNullForeground");
        var punctuationBrush = GetBrush("JsonPunctuationForeground");

        var text = CurrentContext.Document.GetText(line);

        foreach (Match match in Tokens().Matches(text))
        {
            Brush? brush = null;

            if (match.Groups["str"].Success)
            {
                // Key if the next non-whitespace character on this line is ':'
                var afterEnd = match.Index + match.Length;
                var isKey = false;
                for (int i = afterEnd; i < text.Length; i++)
                {
                    if (text[i] == ':') { isKey = true; break; }
                    if (!char.IsWhiteSpace(text[i])) break;
                }
                brush = isKey ? keyBrush : stringBrush;
            }
            else if (match.Groups["kw"].Success)
            {
                brush = match.Value == "null" ? nullBrush : booleanBrush;
            }
            else if (match.Groups["num"].Success)
            {
                brush = numberBrush;
            }
            else if (match.Groups["punct"].Success)
            {
                brush = punctuationBrush;
            }

            if (brush == null)
                continue;

            var start = line.Offset + match.Index;
            var end = start + match.Length;
            ChangeLinePart(start, end, element =>
            {
                element.TextRunProperties.SetForegroundBrush(brush);
            });
        }
    }

    private static Brush? GetBrush(string resourceKey)
    {
        return Application.Current.TryFindResource(resourceKey) as Brush;
    }
}
