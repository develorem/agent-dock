# Heading 1 — Top Level

Regular paragraph text. This is a normal line of text to verify default font, size, and color rendering.

## Heading 2 — Section

### Heading 3 — Subsection

#### Heading 4 — Minor Heading

---

## Inline Formatting

This is **bold text** in the middle of a sentence.

This is *italic text* in the middle of a sentence.

This is ***bold and italic*** together.

This has **multiple** bold **words** in one line.

This has *multiple* italic *words* in one line.

A single **b** bold character. A single *i* italic character.

Underscores: __bold with underscores__ and _italic with underscores_.

## Bold and Italic with Links (MdXaml edge cases)

**[Bold link text](https://example.com)**

*[Italic link text](https://example.com)*

***[Bold italic link text](https://example.com)***

Here is a **[bold link](https://example.com)** in the middle of a sentence.

Here is a *[italic link](https://example.com)* in the middle of a sentence.

- **[Bold link in a list](https://example.com)** — with trailing text
- *[Italic link in a list](https://example.com)* — with trailing text

## Inline Code

Use the `dotnet build` command to compile.

Run `git status` to check for changes.

A backtick in text: the variable `x` is unused.

## Links

[Regular link](https://example.com)

[Link with title](https://example.com "Example Site")

Autolink: https://example.com

## Lists

### Unordered

- First item
- Second item
  - Nested item A
  - Nested item B
    - Deeply nested
- Third item

### Ordered

1. Step one
2. Step two
   1. Sub-step A
   2. Sub-step B
3. Step three

### Mixed Content in Lists

- **Bold item** with regular text after
- *Italic item* with `inline code` after
- A list item with a [link](https://example.com)
- Item with ***bold italic*** formatting

## Blockquotes

> This is a blockquote.

> This is a multi-line blockquote.
>
> It has multiple paragraphs.
>
> > And a nested blockquote inside.

> **Bold in a blockquote** and *italic in a blockquote*.

## Code Blocks

Fenced with language:

```csharp
public class Example
{
    public string Name { get; set; } = "Hello";

    public void DoSomething()
    {
        Console.WriteLine($"Name is: {Name}");
    }
}
```

Fenced without language:

```
plain text code block
no syntax highlighting here
just monospace text
```

Indented code block (4 spaces):

    var x = 42;
    var y = x * 2;
    Console.WriteLine(y);

## Tables

| Feature       | Status | Notes                  |
|---------------|--------|------------------------|
| Bold          | Works  | `**text**`             |
| Italic        | Works  | `*text*`               |
| Code          | Works  | backtick delimited     |
| Bold + Link   | Fixed  | was showing asterisks  |

### Alignment

| Left-aligned | Center-aligned | Right-aligned |
|:-------------|:--------------:|--------------:|
| Left         |     Center     |         Right |
| Data         |      Data      |          Data |

## Horizontal Rules

Above the rule.

---

Between rules.

***

Below the rules.

## Emphasis Edge Cases

Mid-word emphasis: un**frigging**believable (may not render in all parsers).

Asterisks without emphasis: 2 * 3 * 4 = 24

Escaped asterisks: \*not italic\* and \*\*not bold\*\*

## Nested Formatting

> ### Heading inside blockquote
>
> - List inside blockquote
> - With **bold** and `code`
>
> ```
> code block inside blockquote
> ```

## Long Paragraph

Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum.

## Special Characters

Ampersand: &, Less-than: <, Greater-than: >, Quotes: "double" and 'single'

Em dash: — En dash: – Ellipsis: ...

Copyright: (c) Trademark: (tm)
