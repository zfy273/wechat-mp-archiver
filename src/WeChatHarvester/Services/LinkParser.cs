using System.Text.RegularExpressions;

namespace WeChatHarvester.Services;

/// <summary>
/// 从任意粘贴文本里抽出公众号文章链接。
/// 兼容微信"复制链接"得到的带 #rd 形式、http/https、以及被转义过的 &amp;。
/// </summary>
public static class LinkParser
{
    private static readonly Regex UrlRx = new(
        "https?://mp\\.weixin\\.qq\\.com/s(\\?[^\\s\"'<>()\\[\\]]+|/[A-Za-z0-9_-]{6,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>返回规范化后的链接（去重、保序）。</summary>
    public static List<string> Extract(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var normalizedText = text
            .Replace("&amp;", "&")
            .Replace("\\/", "/")
            .Replace("\u200b", "");

        foreach (Match m in UrlRx.Matches(normalizedText))
        {
            var url = Normalize(m.Value);
            if (url == null) continue;
            if (!result.Contains(url)) result.Add(url);
        }
        return result;
    }

    /// <summary>规范化：强制 https、去掉锚点与追踪参数、保留 sn/chksm 等校验参数。</summary>
    public static string Normalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim().TrimEnd('"', '\'', ',', '，', '。', '；', ';', '）', ')');

        var hash = u.IndexOf('#');
        if (hash >= 0) u = u[..hash];

        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            u = "https://" + u[7..];   // "http://" 是 7 个字符

        if (!u.StartsWith("https://mp.weixin.qq.com/s", StringComparison.OrdinalIgnoreCase))
            return null;

        // 形如 /s/AbCdEf123 的短链：没有参数可清，直接规范化返回
        if (!u.StartsWith("https://mp.weixin.qq.com/s?", StringComparison.OrdinalIgnoreCase))
        {
            var shortUrl = u.Split('?')[0];
            return shortUrl.StartsWith("https://mp.weixin.qq.com/s/", StringComparison.OrdinalIgnoreCase) &&
                   shortUrl.Length > "https://mp.weixin.qq.com/s/".Length + 5
                ? shortUrl
                : null;
        }

        var q = u.IndexOf('?');
        if (q < 0) return null;
        var query = u[(q + 1)..];
        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Contains('='))
            .ToList();

        // 至少要有 sn 或 mid 才能定位到一篇具体文章
        var hasSn = parts.Any(p => p.StartsWith("sn=", StringComparison.OrdinalIgnoreCase));
        var hasMid = parts.Any(p => p.StartsWith("mid=", StringComparison.OrdinalIgnoreCase));
        if (!hasSn && !hasMid) return null;

        // 去掉跟踪参数
        var drop = new[] { "scene", "subscene", "clicktime", "enterid", "ascene", "devicetype",
                           "version", "nettype", "abtest_cookie", "key", "pass_ticket", "wx_header",
                           "from", "isappinstalled", "exportkey", "share_token", "sessionid" };
        parts = parts.Where(p =>
        {
            var k = p[..p.IndexOf('=')];
            return !drop.Contains(k, StringComparer.OrdinalIgnoreCase);
        }).ToList();

        return "https://mp.weixin.qq.com/s?" + string.Join("&", parts);
    }

    /// <summary>粗略判断是否是一篇公众号文章链接。</summary>
    public static bool LooksLikeArticle(string s) => Normalize(s) != null;

    public static string ShortTitle(string url)
    {
        // 下载前没有标题，先用可读的占位
        var m = Regex.Match(url, @"[?&]sn=([0-9a-f]+)", RegexOptions.IgnoreCase);
        return m.Success ? "待抓取 " + m.Groups[1].Value[..Math.Min(8, m.Groups[1].Value.Length)] : "待抓取文章";
    }
}
