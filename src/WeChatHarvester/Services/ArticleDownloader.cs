using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using WeChatHarvester.Models;

namespace WeChatHarvester.Services;

public class ArticleUnavailableException : Exception
{
    public ArticleUnavailableException(string m) : base(m) { }
}

public class ArticleBlockedException : Exception
{
    public ArticleBlockedException(string m) : base(m) { }
}

public class DownloadResult
{
    public int Images { get; set; }
    public int Words { get; set; }
    public string Folder { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Owner { get; set; } = "";
    public long PublishAt { get; set; }
    public string PlainText { get; set; } = "";
}

/// <summary>正文抓取 + 资源落地 + 离线 HTML / Markdown / meta 生成。</summary>
public class ArticleDownloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly HtmlParser _parser = new();

    private readonly HttpClient _httpProxy;

    public ArticleDownloader()
    {
        // 与 MpSession 相同：默认直连，直连不通才走系统代理
        _http = CreateClient(useProxy: false);
        _httpProxy = CreateClient(useProxy: true);
    }

    private static HttpClient CreateClient(bool useProxy)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = useProxy
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", MpSession.UA);
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        return c;
    }

    /// <summary>先直连；直连抛网络异常时换系统代理重发（请求对象不可复用，所以传工厂）。</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        var req = make();
        try { return await _http.SendAsync(req, ct); }
        catch (HttpRequestException)
        {
            req.Dispose();
            return await _httpProxy.SendAsync(make(), ct);
        }
    }

    public async Task<DownloadResult> DownloadAsync(
        ArticleRecord rec, string accountDir, AppSettings cfg, string accountName,
        Action<string> log, CancellationToken ct)
    {
        var link = rec.Link.Replace("http://", "https://");
        string html, finalUrl;
        try
        {
            (html, finalUrl) = await FetchTextAsync(link, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ArticleBlockedException("页面请求失败：" + ex.Message);
        }

        if (finalUrl.Contains("/mp/verify") || finalUrl.Contains("wappoc_appmsgcaptcha") || finalUrl.Contains("appmsgcaptcha"))
            throw new ArticleBlockedException("被微信重定向到安全验证页。可在内置浏览器里手动打开该链接通过验证，或稍后重试。");

        if (IsVerificationPage(html))
            throw new ArticleBlockedException("触发微信风控验证，请稍后再试（可在右侧浏览器打开该链接手动通过验证）。");

        if (html.Contains("该内容已被发布者删除") || html.Contains("此内容因违规无法查看") ||
            html.Contains("该公众号已迁移") || html.Contains("内容已被删除"))
            throw new ArticleUnavailableException("内容已被删除或屏蔽。");

        var doc = await _parser.ParseDocumentAsync(html);
        var content = doc.QuerySelector("#js_content") ?? doc.QuerySelector(".rich_media_content");
        if (content == null)
            throw new ArticleUnavailableException("页面中没有正文（链接参数不完整、内容已删除、或该类型内容仅限微信内查看）。");

        var title = Clean(doc.QuerySelector("#activity-name")?.TextContent);
        if (string.IsNullOrWhiteSpace(title))
            title = doc.QuerySelector("meta[property='og:title']")?.GetAttribute("content") ?? "";
        if (string.IsNullOrWhiteSpace(title)) title = rec.Title;

        var author = Clean(doc.QuerySelector("#js_name")?.TextContent);
        var publishText = Clean(doc.QuerySelector("#publish_time")?.TextContent);
        var ts = rec.PublishAt > 0
            ? DateTimeOffset.FromUnixTimeSeconds(rec.PublishAt).ToLocalTime().DateTime
            : ParseTimeFromHtml(html) ?? DateTime.Now;

        // 目录
        var folderName = $"{ts:yyyy-MM-dd}_{AppSettings.SafeName(title)}_{rec.Id[..6]}";
        var folder = Path.Combine(accountDir, "articles", folderName);
        var assets = Path.Combine(folder, "assets");
        if (cfg.DownloadImages) Directory.CreateDirectory(assets);
        Directory.CreateDirectory(folder);

        // 清理脚本样式
        foreach (var s in content.QuerySelectorAll("script, style, noscript").ToList())
            s.Parent?.RemoveChild(s);

        // 微信正文默认 visibility:hidden，靠页面脚本还原；脚本被剥离后必须手动解除，否则正文全白
        RevealHidden(content);

        int imgCount = 0;
        if (cfg.DownloadImages)
        {
            foreach (var img in content.QuerySelectorAll("img").ToList())
            {
                ct.ThrowIfCancellationRequested();
                var src = FirstNonEmpty(img.GetAttribute("data-src"), img.GetAttribute("data-original"), img.GetAttribute("src"));
                if (string.IsNullOrWhiteSpace(src) || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var local = await TryDownloadAsync(src, assets, "img", link, ct);
                img.RemoveAttribute("data-src");
                img.RemoveAttribute("data-original");
                img.RemoveAttribute("data-crop");
                img.SetAttribute("src", local ?? src);
                img.SetAttribute("style", "max-width:100%;height:auto;display:block;margin:12px auto;");
                if (local != null) imgCount++;
                if (cfg.ImageDelayMs > 0) await Task.Delay(cfg.ImageDelayMs, ct);
            }
        }

        // 语音
        if (cfg.DownloadAudio)
        {
            foreach (var v in content.QuerySelectorAll("mpvoice").ToList())
            {
                var fid = v.GetAttribute("voice_encode_fileid");
                var name = v.GetAttribute("name") ?? "语音";
                var note = doc.CreateElement("div");
                note.ClassName = "media-note";
                if (!string.IsNullOrWhiteSpace(fid))
                {
                    var url = "https://res.wx.qq.com/voice/getvoice?mediaid=" + Uri.EscapeDataString(fid);
                    var local = await TryDownloadAsync(url, assets, "voice", link, ct, forceExt: ".mp3");
                    note.TextContent = local != null
                        ? $"🎧 语音：{name} → assets/{Path.GetFileName(local)}"
                        : $"🎧 语音：{name}（原地址 {url}）";
                }
                else note.TextContent = $"🎧 语音：{name}（未能取得地址）";
                v.Parent?.ReplaceChild(note, v);
            }
        }

        // 视频 / 其他内嵌
        foreach (var iframe in content.QuerySelectorAll("iframe").ToList())
        {
            var src = FirstNonEmpty(iframe.GetAttribute("data-src"), iframe.GetAttribute("src"));
            if (string.IsNullOrWhiteSpace(src))
            {
                iframe.Parent?.RemoveChild(iframe);
                continue;
            }
            var note = doc.CreateElement("div");
            note.ClassName = "media-note";
            var a = doc.CreateElement("a");
            a.SetAttribute("href", src);
            a.TextContent = "▶ 视频无法离线保存，点击原页面地址";
            note.AppendChild(a);
            iframe.Parent?.ReplaceChild(note, iframe);
        }

        foreach (var mv in content.QuerySelectorAll("mpvideo, mp-common-videosnap").ToList())
        {
            var note = doc.CreateElement("div");
            note.ClassName = "media-note";
            note.TextContent = "▶ 视频内容（微信视频号/内嵌视频不支持离线保存）";
            mv.Parent?.ReplaceChild(note, mv);
        }

        var contentHtml = content.OuterHtml;
        var plain = content.TextContent ?? "";
        var words = CountWords(plain);

        // 写文件
        var htmlPath = Path.Combine(folder, "index.html");
        File.WriteAllText(htmlPath, BuildReaderHtml(title, accountName, ts.ToString("yyyy-MM-dd HH:mm"), author, link, contentHtml),
            new UTF8Encoding(false));

        if (cfg.ExportMarkdown)
        {
            var md = new StringBuilder();
            md.Append("# ").Append(title).Append("\n\n");
            md.Append("> ").Append(accountName).Append(" · ").Append(ts.ToString("yyyy-MM-dd HH:mm"));
            if (!string.IsNullOrWhiteSpace(author)) md.Append(" · ").Append(author);
            md.Append("\n>\n> 原文：").Append(link).Append("\n\n");
            md.Append("---\n\n");
            md.Append(HtmlToMarkdown.Convert(content));
            File.WriteAllText(Path.Combine(folder, "article.md"), md.ToString(), new UTF8Encoding(false));
        }

        var meta = new
        {
            id = rec.Id,
            title,
            author,
            account = accountName,
            link,
            publishAt = rec.PublishAt,
            publishText,
            publishTime = ts.ToString("yyyy-MM-dd HH:mm:ss"),
            archivedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            images = imgCount,
            words
        };
        File.WriteAllText(Path.Combine(folder, "meta.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), new UTF8Encoding(false));

        if (string.IsNullOrWhiteSpace(rec.Title) || rec.Title.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            rec.Title = title;
        rec.Folder = folderName;
        rec.Owner = Clean(doc.QuerySelector("#js_name")?.TextContent);

        return new DownloadResult
        {
            Images = imgCount,
            Words = words,
            Folder = folderName,
            Title = title,
            Author = author,
            Owner = rec.Owner,
            PublishAt = rec.PublishAt > 0 ? rec.PublishAt
                : new DateTimeOffset(ts).ToUnixTimeSeconds(),
            PlainText = plain.Length > 4000 ? plain[..4000] : plain
        };
    }

    /// <summary>把 style 里的 visibility:hidden / opacity:0 清掉（微信懒加载与首屏隐藏的常见写法）。</summary>
    private static void RevealHidden(IElement root)
    {
        var queue = new Queue<IElement>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var el = queue.Dequeue();
            var st = el.GetAttribute("style");
            if (!string.IsNullOrEmpty(st) &&
                (st.Contains("visibility", StringComparison.OrdinalIgnoreCase) ||
                 st.Contains("opacity", StringComparison.OrdinalIgnoreCase)))
            {
                var cleaned = Regex.Replace(st, @"visibility\s*:\s*hidden\s*;?", "", RegexOptions.IgnoreCase);
                cleaned = Regex.Replace(cleaned, @"opacity\s*:\s*0(\.0+)?\s*;?", "", RegexOptions.IgnoreCase);
                el.SetAttribute("style", cleaned);
            }
            foreach (var c in el.Children) queue.Enqueue(c);
        }
    }

    private async Task<(string html, string finalUrl)> FetchTextAsync(string url, CancellationToken ct)
    {
        using var resp = await SendAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Get, url);
            r.Headers.TryAddWithoutValidation("Referer", "https://mp.weixin.qq.com/");
            return r;
        }, ct);
        resp.EnsureSuccessStatusCode();
        var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
        return (await resp.Content.ReadAsStringAsync(ct), finalUrl);
    }

    private static bool IsVerificationPage(string html)
        => html.Contains("环境异常") && (html.Contains("完成验证") || html.Contains("拖动下方滑块"));

    private static string FirstNonEmpty(params string[] vals)
        => vals.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string Clean(string s)
        => Regex.Replace((s ?? "").Replace("\u00a0", " "), @"\s+", " ").Trim();

    private static DateTime? ParseTimeFromHtml(string html)
    {
        var m = Regex.Match(html, @"var\s+ct\s*=\s*""(\d{10})""");
        if (m.Success && long.TryParse(m.Groups[1].Value, out var sec))
            return DateTimeOffset.FromUnixTimeSeconds(sec).ToLocalTime().DateTime;
        m = Regex.Match(html, @"\bct\s*=\s*""(\d{10})""");
        if (m.Success && long.TryParse(m.Groups[1].Value, out var sec2))
            return DateTimeOffset.FromUnixTimeSeconds(sec2).ToLocalTime().DateTime;
        return null;
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var cjk = text.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        var latin = Regex.Matches(text, @"[A-Za-z0-9]+").Count;
        return cjk + latin;
    }

    /// <summary>下载单个资源到 assets；失败返回 null（调用方保留远程地址）。</summary>
    private async Task<string> TryDownloadAsync(string url, string assetsDir, string prefix, string referer,
        CancellationToken ct, string forceExt = null)
    {
        try
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
            Directory.CreateDirectory(assetsDir);

            using var resp = await SendAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Get, url);
                r.Headers.TryAddWithoutValidation("Referer", referer);
                return r;
            }, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0) return null;

            var ext = forceExt ?? ExtFrom(url, resp.Content.Headers.ContentType?.MediaType);
            var name = prefix + "_" + Hash8(url) + ext;
            var full = Path.Combine(assetsDir, name);
            if (!File.Exists(full)) await File.WriteAllBytesAsync(full, bytes, ct);
            return "assets/" + name;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string Hash8(string s)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..10];
    }

    private static string ExtFrom(string url, string contentType)
    {
        var m = Regex.Match(url, @"[?&]wx_fmt=([a-zA-Z0-9]+)");
        if (m.Success)
        {
            var f = m.Groups[1].Value.ToLowerInvariant();
            if (f == "jpeg") f = "jpg";
            return "." + f;
        }
        if (!string.IsNullOrEmpty(contentType))
        {
            if (contentType.Contains("png")) return ".png";
            if (contentType.Contains("jpeg") || contentType.Contains("jpg")) return ".jpg";
            if (contentType.Contains("gif")) return ".gif";
            if (contentType.Contains("webp")) return ".webp";
            if (contentType.Contains("svg")) return ".svg";
            if (contentType.Contains("mpeg") || contentType.Contains("mp3")) return ".mp3";
        }
        m = Regex.Match(url, @"\.(jpg|jpeg|png|gif|webp|bmp|svg)", RegexOptions.IgnoreCase);
        if (m.Success) return "." + m.Groups[1].Value.ToLowerInvariant().Replace("jpeg", "jpg");
        return ".jpg";
    }

    private const string ReaderTemplate = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>__TITLE__</title>
<style>
  * { box-sizing: border-box; }
  body { margin:0; background:#f2f4f7; color:#20262e;
         font-family: -apple-system, ""Segoe UI"", ""Microsoft YaHei"", ""PingFang SC"", sans-serif; }
  .paper { max-width: 760px; margin: 0 auto; background:#fff; min-height:100vh;
           padding: 52px 30px 96px; box-shadow: 0 0 70px rgba(16,24,40,.07); }
  h1.title { font-size: 25px; line-height: 1.5; margin: 0 0 16px; font-weight: 600; letter-spacing:.01em; }
  .meta { font-size: 13px; color: #9099a6; padding-bottom: 18px; border-bottom: 1px solid #eef0f3; margin-bottom: 28px; }
  .meta .dot { margin: 0 7px; color:#cfd5de; }
  .content { font-size: 16.5px; line-height: 1.85; letter-spacing: .012em; word-break: break-word; }
  .content img { max-width: 100% !important; height: auto !important; display:block; margin: 14px auto; border-radius: 3px; }
  .content p, .content section { margin: 0 0 16px; }
  .content a { color: #0a7d55; word-break: break-all; }
  .content table { border-collapse: collapse; width: 100%; font-size: 15px; }
  .content td, .content th { border: 1px solid #e3e7ec; padding: 7px 9px; }
  .media-note { background:#f5f7f9; border-left:3px solid #07c160; padding:10px 14px; margin:14px 0;
                font-size:14px; color:#5b6472; border-radius: 0 4px 4px 0; }
  .media-note a { color:#0a7d55; }
  footer { margin-top: 46px; padding-top: 16px; border-top: 1px solid #eef0f3; font-size: 12px; color: #a7aeba; word-break: break-all; }
  @media print { body { background:#fff; } .paper { box-shadow:none; padding:0; max-width:none; } }
</style>
</head>
<body>
<div class=""paper"">
  <h1 class=""title"">__TITLE__</h1>
  <div class=""meta"">__ACCOUNT__<span class=""dot"">·</span>__TIME____AUTHOR__</div>
  <div class=""content"">__CONTENT__</div>
  <footer>原文链接：<a href=""__LINK__"">__LINK__</a><br>本文件由「公众号文章采集器」于 __NOW__ 离线归档，仅供个人学习留档使用。</footer>
</div>
</body>
</html>";

    private static string BuildReaderHtml(string title, string account, string time, string author, string link, string content)
    {
        var authorPart = string.IsNullOrWhiteSpace(author) ? "" : "<span class=\"dot\">·</span>" + WebUtility.HtmlEncode(author);
        return ReaderTemplate
            .Replace("__TITLE__", WebUtility.HtmlEncode(title))
            .Replace("__ACCOUNT__", WebUtility.HtmlEncode(account))
            .Replace("__TIME__", time)
            .Replace("__AUTHOR__", authorPart)
            .Replace("__LINK__", WebUtility.HtmlEncode(link))
            .Replace("__NOW__", DateTime.Now.ToString("yyyy-MM-dd HH:mm"))
            .Replace("__CONTENT__", content);
    }

    public void Dispose()
    {
        _http.Dispose();
        _httpProxy.Dispose();
    }
}
