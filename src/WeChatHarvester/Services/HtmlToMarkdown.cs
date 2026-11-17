using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace WeChatHarvester.Services;

/// <summary>把公众号正文 DOM 转成 Markdown（保留标题/段落/图片/列表/表格/引用）。</summary>
public static class HtmlToMarkdown
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "section", "div", "article", "center", "blockquote", "ul", "ol", "li",
        "h1", "h2", "h3", "h4", "h5", "h6", "table", "figure", "pre", "hr", "header", "footer"
    };

    public static string Convert(IElement root)
    {
        var sb = new StringBuilder();
        foreach (var node in root.ChildNodes) Block(node, sb);
        var text = sb.ToString();
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim() + "\n";
    }

    private static bool HasBlockChild(IElement el)
    {
        foreach (var c in el.Children)
            if (BlockTags.Contains(c.LocalName)) return true;
        return false;
    }

    private static void Block(INode node, StringBuilder sb)
    {
        if (node is IText t)
        {
            var s = t.TextContent.Trim();
            if (s.Length > 0) sb.Append(s).Append("\n\n");
            return;
        }
        if (node is not IElement el) return;

        switch (el.LocalName.ToLowerInvariant())
        {
            case "script":
            case "style":
            case "noscript":
                return;

            case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
            {
                var level = el.LocalName[1] - '0';
                var txt = Inline(el).Trim();
                if (txt.Length > 0) sb.Append('#', level).Append(' ').Append(txt).Append("\n\n");
                return;
            }

            case "p":
            {
                var txt = Inline(el).Trim();
                if (txt.Length > 0) sb.Append(txt).Append("\n\n");
                return;
            }

            case "blockquote":
            {
                var inner = new StringBuilder();
                foreach (var c in el.ChildNodes) Block(c, inner);
                var lines = inner.ToString().Trim().Split('\n');
                foreach (var line in lines)
                    sb.Append("> ").Append(line.TrimEnd()).Append('\n');
                sb.Append('\n');
                return;
            }

            case "ul":
            case "ol":
            {
                int i = 1;
                foreach (var li in el.Children.Where(c => c.LocalName.Equals("li", StringComparison.OrdinalIgnoreCase)))
                {
                    var marker = el.LocalName.Equals("ol", StringComparison.OrdinalIgnoreCase) ? $"{i++}. " : "- ";
                    var txt = Inline(li).Trim();
                    if (txt.Length > 0) sb.Append(marker).Append(txt).Append('\n');
                    foreach (var nested in li.Children.Where(c => c.LocalName is "ul" or "ol"))
                    {
                        var n = new StringBuilder();
                        Block(nested, n);
                        foreach (var line in n.ToString().Trim().Split('\n'))
                            if (line.Trim().Length > 0) sb.Append("  ").Append(line.TrimEnd()).Append('\n');
                    }
                }
                sb.Append('\n');
                return;
            }

            case "li":
            {
                var txt = Inline(el).Trim();
                if (txt.Length > 0) sb.Append("- ").Append(txt).Append('\n');
                return;
            }

            case "table":
            {
                AppendTable(el, sb);
                return;
            }

            case "hr":
                sb.Append("\n---\n\n");
                return;

            case "pre":
            {
                sb.Append("```\n").Append(el.TextContent.Trim('\n')).Append("\n```\n\n");
                return;
            }

            default:
            {
                if (HasBlockChild(el))
                {
                    foreach (var c in el.ChildNodes) Block(c, sb);
                }
                else
                {
                    var txt = Inline(el).Trim();
                    if (txt.Length > 0) sb.Append(txt).Append("\n\n");
                }
                return;
            }
        }
    }

    private static void AppendTable(IElement table, StringBuilder sb)
    {
        var rows = table.QuerySelectorAll("tr").ToList();
        if (rows.Count == 0) return;
        var matrix = rows.Select(r => r.QuerySelectorAll("td,th").Select(c => Cell(c)).ToList()).ToList();
        var cols = matrix.Max(r => r.Count);
        if (cols == 0) return;

        sb.Append('\n');
        for (int i = 0; i < matrix.Count; i++)
        {
            var row = matrix[i];
            while (row.Count < cols) row.Add("");
            sb.Append("| ").Append(string.Join(" | ", row)).Append(" |\n");
            if (i == 0)
                sb.Append('|').Append(string.Join("|", Enumerable.Repeat(" --- ", cols))).Append("|\n");
        }
        sb.Append('\n');

        static string Cell(IElement c)
            => c.TextContent.Replace("\n", " ").Replace("|", "\\|").Trim();
    }

    private static string Inline(INode node)
    {
        if (node is IText t) return t.TextContent;
        if (node is not IElement el) return "";

        switch (el.LocalName.ToLowerInvariant())
        {
            case "br":
                return "  \n";
            case "img":
            {
                var src = el.GetAttribute("src") ?? "";
                var alt = el.GetAttribute("alt") ?? "";
                return string.IsNullOrWhiteSpace(src) ? "" : $"\n\n![{alt}]({src})\n\n";
            }
            case "a":
            {
                var href = el.GetAttribute("href") ?? "";
                var txt = Children(el).Trim();
                if (string.IsNullOrWhiteSpace(href) || href.StartsWith("javascript", StringComparison.OrdinalIgnoreCase))
                    return txt;
                if (string.IsNullOrWhiteSpace(txt)) txt = href;
                return $"[{txt}]({href})";
            }
            case "strong":
            case "b":
            {
                var txt = Children(el).Trim();
                return txt.Length == 0 ? "" : $"**{txt}**";
            }
            case "em":
            case "i":
            {
                var txt = Children(el).Trim();
                return txt.Length == 0 ? "" : $"*{txt}*";
            }
            case "code":
                return $"`{el.TextContent}`";
            case "script":
            case "style":
                return "";
            default:
                return Children(el);
        }
    }

    private static string Children(IElement el)
    {
        var sb = new StringBuilder();
        foreach (var c in el.ChildNodes) sb.Append(Inline(c));
        return sb.ToString();
    }
}
