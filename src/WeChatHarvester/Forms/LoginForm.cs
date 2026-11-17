using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WeChatHarvester.Models;
using WeChatHarvester.Services;

namespace WeChatHarvester.Forms;

/// <summary>
/// 登录公众号后台（可选步骤）。
/// 用 WinForms 原生控件 + WebView2 承载扫码页；关窗时把 token 与 cookie 交给 MpSession。
/// </summary>
public class LoginForm : Form
{
    private readonly MpSession _session;
    private readonly AppSettings _cfg;

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = true, Text = "正在打开公众号后台…", Anchor = AnchorStyles.Left };
    private readonly Button _btnDone = new() { Text = "已完成登录", Width = 110, Height = 27, DialogResult = DialogResult.OK };
    private readonly Button _btnRefresh = new() { Text = "刷新页面", Width = 90, Height = 27 };
    private readonly Button _btnCancel = new() { Text = "取消", Width = 80, Height = 27, DialogResult = DialogResult.Cancel };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1500 };

    private bool _ready;
    private bool _logged;

    public LoginForm(MpSession session, AppSettings cfg)
    {
        _session = session;
        _cfg = cfg;

        Text = "登录公众号后台";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1000, 720);
        MinimumSize = new Size(720, 520);
        ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont;

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(10, 9, 10, 9) };
        _btnDone.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _btnCancel.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _btnRefresh.Anchor = AnchorStyles.Left | AnchorStyles.Top;

        _btnCancel.Location = new Point(bottom.Width - _btnCancel.Width - 10, 9);
        _btnDone.Location = new Point(_btnCancel.Left - _btnDone.Width - 8, 9);
        _btnRefresh.Location = new Point(10, 9);
        _status.Location = new Point(_btnRefresh.Right + 16, 15);

        bottom.Controls.AddRange(new Control[] { _status, _btnRefresh, _btnCancel, _btnDone });

        Controls.Add(_web);
        Controls.Add(bottom);

        _btnRefresh.Click += (_, __) => { try { _web.CoreWebView2?.Reload(); } catch { } };
        _btnDone.Click += OnDone;
        _timer.Tick += async (_, __) => await DetectAsync();

        Shown += async (_, __) => await InitAsync();
        FormClosed += (_, __) =>
        {
            _timer.Stop();
            try { _web.Dispose(); } catch { }
        };
    }

    private async Task InitAsync()
    {
        try
        {
            var profile = Path.Combine(_cfg.OutputRoot, "_webview2");
            Directory.CreateDirectory(profile);
            var env = await CoreWebView2Environment.CreateAsync(null, profile, null);
            await _web.EnsureCoreWebView2Async(env);

            var s = _web.CoreWebView2.Settings;
            s.IsStatusBarEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;

            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess) _status.Text = "页面加载失败：" + e.WebErrorStatus;
                else if (!_logged) _status.Text = "请用微信扫码登录";
                await DetectAsync();
            };
            _web.CoreWebView2.Navigate("https://mp.weixin.qq.com/");
            _ready = true;
            _timer.Start();
        }
        catch (Exception ex)
        {
            _status.Text = "浏览器组件初始化失败";
            MessageBox.Show(this, ex.Message, "无法打开浏览器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task DetectAsync()
    {
        if (!_ready || _logged || _web.CoreWebView2 == null) return;
        try
        {
            var url = _web.Source?.ToString() ?? "";
            var m = Regex.Match(url, @"[?&]token=(\d{5,})");
            if (!m.Success) return;

            _session.Token = m.Groups[1].Value;
            await RefreshCookiesAsync();
            if (string.IsNullOrWhiteSpace(_session.CookieHeader)) return;

            _logged = true;
            _status.Text = "已登录，可以点“已完成登录”";
            _btnDone.Enabled = true;
        }
        catch { }
    }

    private async Task RefreshCookiesAsync()
    {
        if (_web.CoreWebView2 == null) return;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uri in new[] { "https://mp.weixin.qq.com", "https://mp.weixin.qq.com/cgi-bin/home" })
        {
            try
            {
                foreach (var c in await _web.CoreWebView2.CookieManager.GetCookiesAsync(uri))
                    dict[c.Name] = c.Value;
            }
            catch { }
        }
        _session.SetCookies(dict);
    }

    private async void OnDone(object sender, EventArgs e)
    {
        if (!_logged)
        {
            await DetectAsync();
            if (!_logged)
            {
                MessageBox.Show(this,
                    "还没有检测到登录状态。请先在页面里用微信扫码登录，成功后再点“已完成登录”。",
                    "尚未登录", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None;
                return;
            }
        }
        await RefreshCookiesAsync();
    }
}
