using System.Net;
using System.Net.Http;
using System.Text.Json;
using WeChatHarvester.Models;

namespace WeChatHarvester.Services;

public enum MpError { None, LoginExpired, FreqControl, Captcha, Unknown }

public class MpException : Exception
{
    public MpError Kind { get; }
    public MpException(MpError kind, string message) : base(message) => Kind = kind;
}

public class CatalogPage
{
    public long Total { get; set; } = -1;
    public List<ArticleRecord> Items { get; } = new();
}

/// <summary>
/// 公众号后台（mp.weixin.qq.com）接口客户端。
/// 复用 WebView2 里用户自己扫码登录后的 token + cookie，走的是后台「超链接-选择其他公众号」所用的公开接口，
/// 这是目前唯一能稳定翻页获取某公众号全部历史文章目录的途径。
/// </summary>
public class MpSession : IDisposable
{
    public const string UA =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";

    private readonly HttpClient _http;

    public string Token { get; set; } = "";
    public string CookieHeader { get; private set; } = "";
    public string NickName { get; set; } = "";

    public bool HasSession => !string.IsNullOrWhiteSpace(Token) && !string.IsNullOrWhiteSpace(CookieHeader);

    /// <summary>日志回调（由界面挂上）。</summary>
    public Action<string> Log { get; set; }

    private readonly HttpClient _httpProxy;   // 走系统代理（兜底）
    private bool _warmedUp;

    public MpSession()
    {
        // 关键：微信后台接口对出口 IP 极敏感，走代理几乎必被判 200013（freq control）。
        // 所以默认直连，只有直连网络不通时才回退到系统代理。
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
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        c.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        return c;
    }

    public void SetCookies(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        CookieHeader = string.Join("; ", pairs.Where(p => !string.IsNullOrEmpty(p.Key)).Select(p => $"{p.Key}={p.Value}"));
    }

    public string HomeUrl => $"https://mp.weixin.qq.com/cgi-bin/home?t=home/index&lang=zh_CN&token={Token}";
    public string AppMsgUrl => $"https://mp.weixin.qq.com/cgi-bin/appmsg?t=media/appmsg_edit&action=edit&type=10&isMul=1&token={Token}&lang=zh_CN";

    private HttpRequestMessage Build(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Referer", AppMsgUrl);
        if (!string.IsNullOrEmpty(CookieHeader))
            req.Headers.TryAddWithoutValidation("Cookie", CookieHeader);
        return req;
    }

    private async Task<string> SendAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var req = Build(url);
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new MpException(MpError.Unknown, $"接口返回 HTTP {(int)resp.StatusCode}");
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            return await SendAsync(_http, url, ct);          // 直连优先
        }
        catch (HttpRequestException ex)
        {
            Log?.Invoke($"[网络] 直连失败（{ex.Message}），改用系统代理重试…");
            return await SendAsync(_httpProxy, url, ct);     // 代理兜底
        }
    }

    /// <summary>正式拉列表前先访问一次图文编辑页，把该模块的会话"预热"上。</summary>
    private async Task WarmUpAsync(CancellationToken ct)
    {
        if (_warmedUp) return;
        _warmedUp = true;
        try
        {
            using var req = Build(AppMsgUrl);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            _ = resp.StatusCode;
        }
        catch { }
    }

    private static void ThrowIfNotJson(string text)
    {
        var t = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (t.StartsWith("<"))
            throw new MpException(MpError.LoginExpired, "登录态已失效（接口返回了网页而不是数据），请重新扫码登录。");
    }

    /// <summary>搜索公众号，拿到 fakeid。</summary>
    public async Task<List<AccountHit>> SearchAccountAsync(string keyword, CancellationToken ct)
    {
        if (!HasSession) throw new MpException(MpError.LoginExpired, "尚未登录公众号后台。");
        var url = "https://mp.weixin.qq.com/cgi-bin/searchbiz?action=search_biz&begin=0&count=5" +
                  "&query=" + Uri.EscapeDataString(keyword) +
                  "&token=" + Token + "&lang=zh_CN&f=json&ajax=1";

        var text = await GetAsync(url, ct);
        ThrowIfNotJson(text);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var (ret, msg) = ReadResp(root);
        if (ret == 200013) throw new MpException(MpError.FreqControl, $"搜索过于频繁（ret=200013 {msg}）".Trim());
        if (ret == 200003) throw new MpException(MpError.LoginExpired, "登录态已失效，请重新扫码登录。");
        if (ret != 0) throw new MpException(MpError.Unknown, $"搜索失败 ret={ret} {msg}");

        var list = new List<AccountHit>();
        if (root.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray())
            {
                list.Add(new AccountHit
                {
                    FakeId = Str(it, "fakeid"),
                    NickName = Str(it, "nickname"),
                    Alias = Str(it, "alias"),
                    HeadImg = Str(it, "round_head_img"),
                    Signature = Str(it, "signature")
                });
            }
        }
        return list;
    }

    /// <summary>分页拉取文章目录。</summary>
    public async Task<CatalogPage> FetchPageAsync(string fakeId, int begin, int count, CancellationToken ct)
    {
        if (!HasSession) throw new MpException(MpError.LoginExpired, "尚未登录公众号后台。");
        await WarmUpAsync(ct);
        var url = "https://mp.weixin.qq.com/cgi-bin/appmsg?action=list_ex" +
                  "&begin=" + begin + "&count=" + count +
                  "&fakeid=" + Uri.EscapeDataString(fakeId) +
                  "&type=9&query=&token=" + Token + "&lang=zh_CN&f=json&ajax=1";

        var text = await GetAsync(url, ct);
        ThrowIfNotJson(text);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var (ret, msg) = ReadResp(root);

        if (ret == 200013) throw new MpException(MpError.FreqControl, $"操作过于频繁（ret=200013 {msg}）".Trim());
        if (ret == 200003) throw new MpException(MpError.LoginExpired, "登录态已失效，请重新扫码登录。");
        if (ret == 200002) throw new MpException(MpError.Unknown, "参数有误（fakeid 可能不对）。");
        if (ret != 0) throw new MpException(MpError.Unknown, $"接口返回 ret={ret} {msg}");

        var page = new CatalogPage();
        if (root.TryGetProperty("app_msg_cnt", out var cnt))
        {
            if (cnt.ValueKind == JsonValueKind.Number && cnt.TryGetInt64(out var n)) page.Total = n;
            else if (cnt.ValueKind == JsonValueKind.String && long.TryParse(cnt.GetString(), out var n2)) page.Total = n2;
        }

        if (root.TryGetProperty("app_msg_list", out var listElem) && listElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in listElem.EnumerateArray())
            {
                var link = Str(it, "link");
                if (string.IsNullOrEmpty(link)) continue;
                link = link.Split('#')[0];
                if (!link.Contains("mp.weixin.qq.com/s")) continue;

                page.Items.Add(new ArticleRecord
                {
                    Link = link,
                    Title = WebUtility.HtmlDecode(Str(it, "title")).Trim(),
                    Digest = WebUtility.HtmlDecode(Str(it, "digest")).Trim(),
                    Cover = Str(it, "cover"),
                    PublishAt = Num(it, "create_time"),
                    Status = ArticleStatus.Pending,
                    Source = "目录"
                });
            }
        }
        return page;
    }

    private static (int ret, string msg) ReadResp(JsonElement root)
    {
        int ret = 0; string msg = "";
        if (root.TryGetProperty("base_resp", out var br))
        {
            if (br.TryGetProperty("ret", out var rv) && rv.ValueKind == JsonValueKind.Number) ret = rv.GetInt32();
            if (br.TryGetProperty("err_msg", out var mv) && mv.ValueKind == JsonValueKind.String) msg = mv.GetString() ?? "";
        }
        return (ret, msg);
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static long Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var n2)) return n2;
        return 0;
    }

    public void Dispose()
    {
        _http.Dispose();
        _httpProxy.Dispose();
    }
}
