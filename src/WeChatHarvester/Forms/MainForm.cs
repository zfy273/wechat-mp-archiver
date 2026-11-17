using System.Diagnostics;
using System.Net;
using System.IO.Compression;
using System.Text;
using WeChatHarvester.Models;
using WeChatHarvester.Services;

namespace WeChatHarvester.Forms;

/// <summary>
/// 主窗口。全部使用 WinForms 自带控件（MenuStrip / TabControl / GroupBox / ListView /
/// DateTimePicker / StatusStrip 等），不做任何自绘与配色，保持 Windows 原生外观。
/// </summary>
public class MainForm : Form
{
    private readonly AppSettings _cfg;
    private readonly Library _lib;
    private readonly ArticleDownloader _dl = new();
    private MpSession _mp = new();

    private CancellationTokenSource _cts;
    private bool _busy;
    private bool _refreshing;
    private string _fakeId;
    private long _total = -1;
    private int _nextBegin;

    private readonly HashSet<string> _checkedIds = new();
    private readonly Dictionary<string, ListViewItem> _map = new();

    // ---- 目录页 ----
    private Label _lblLogin = new();
    private TextBox _txtAccount = new();
    private DateTimePicker _dtFrom = new();
    private DateTimePicker _dtTo = new();
    private ListView _lvCatalog = new();
    private TextBox _txtSearch = new();
    private ComboBox _cboStatus = new();
    private Button _btnDownloadChecked = new();
    private Button _btnFetchOnly = new();
    private Button _btnFetchDownload = new();
    private Button _btnStop = new();
    private Label _lblListCount = new();

    // ---- 链接页 ----
    private TextBox _txtLinks = new();
    private Label _lblLinkCount = new();

    // ---- 设置页 ----
    private TextBox _txtOut = new();
    private TextBox _txtAccountCfg = new();
    private TextBox _txtImgDelay = new();
    private TextBox _txtPageSize = new();
    private TextBox _txtDelay = new();
    private CheckBox _chkImages = new();
    private CheckBox _chkAudio = new();
    private CheckBox _chkMd = new();

    // ---- 输出与状态 ----
    private RichTextBox _log = new();
    private ToolStripStatusLabel _lblStatus = new();
    private ToolStripStatusLabel _lblCounts = new();
    private ToolStripProgressBar _progress = new();
    private SplitContainer _split = new();

    public MainForm()
    {
        _cfg = AppSettings.Load();
        _lib = new Library(_cfg.AccountDir);
        _lib.Load();

        Text = "公众号文章采集器";
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1180, 800);
        MinimumSize = new Size(1000, 660);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        BuildUi();
        LoadSettingsToUi();
        RefreshList();
        UpdateCounts();

        _mp.Log = msg => AppendLog(msg, LogLevel.Warn);

        AppendLog("就绪。要抓单篇文章，用「链接导入」页；要抓整个目录，用「公众号目录」页先登录。", LogLevel.Info);
        WarnIfProxy();
    }

    // =====================================================================
    //  界面搭建
    // =====================================================================

    private void BuildUi()
    {
        var menu = BuildMenu();
        var status = BuildStatus();

        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Horizontal;
        _split.FixedPanel = FixedPanel.Panel2;
        _split.SplitterWidth = 6;
        _split.Panel1.Controls.Add(BuildTabs());
        _split.Panel2.Controls.Add(BuildLogPanel());

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(menu, 0, 0);
        root.Controls.Add(_split, 0, 1);
        root.Controls.Add(status, 0, 2);
        Controls.Add(root);
        MainMenuStrip = menu;
    }

    private MenuStrip BuildMenu()
    {
        var m = new MenuStrip { Dock = DockStyle.Fill };

        var file = new ToolStripMenuItem("文件(&F)");
        file.DropDownItems.Add(Item("打开归档目录(&O)", (_, __) => OpenOutput()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("退出(&X)", (_, __) => Close()));

        var collect = new ToolStripMenuItem("采集(&C)");
        collect.DropDownItems.Add(Item("登录公众号后台…(&L)", (_, __) => OpenLogin()));
        collect.DropDownItems.Add(Item("拉取文章目录(&F)", async (_, __) => await StartFetch(false)));
        collect.DropDownItems.Add(Item("拉取并下载(&D)", async (_, __) => await StartFetch(true)));
        collect.DropDownItems.Add(new ToolStripSeparator());
        collect.DropDownItems.Add(Item("下载勾选的文章(&K)", async (_, __) => await DownloadChecked()));
        collect.DropDownItems.Add(Item("停止(&S)", (_, __) => StopWork()));

        var help = new ToolStripMenuItem("帮助(&H)");
        help.DropDownItems.Add(Item("使用说明(&H)", (_, __) => ShowHelp()));
        help.DropDownItems.Add(Item("关于(&A)", (_, __) => ShowAbout()));

        m.Items.Add(file);
        m.Items.Add(collect);
        m.Items.Add(help);
        return m;

        static ToolStripMenuItem Item(string text, EventHandler onClick)
        {
            var i = new ToolStripMenuItem(text);
            i.Click += onClick;
            return i;
        }
    }

    private StatusStrip BuildStatus()
    {
        var s = new StatusStrip { Dock = DockStyle.Fill, SizingGrip = true };
        _lblStatus.Text = "就绪";
        _lblStatus.Spring = true;
        _lblStatus.TextAlign = ContentAlignment.MiddleLeft;

        _lblCounts.Text = "";
        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Value = 0;
        _progress.Width = 160;

        s.Items.Add(_lblStatus);
        s.Items.Add(_progress);
        s.Items.Add(_lblCounts);
        return s;
    }

    private Control BuildTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 4) };
        tabs.TabPages.Add(BuildCatalogPage());
        tabs.TabPages.Add(BuildLinksPage());
        tabs.TabPages.Add(BuildSettingsPage());
        return tabs;
    }

    // ---------------- 目录页 ----------------

    private TabPage BuildCatalogPage()
    {
        var page = new TabPage("公众号目录") { Padding = new Padding(6) };

        var tl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        tl.Controls.Add(BuildLoginGroup(), 0, 0);
        tl.Controls.Add(BuildFetchGroup(), 0, 1);
        tl.Controls.Add(BuildListGroup(), 0, 2);

        page.Controls.Add(tl);
        return page;
    }

    private GroupBox BuildLoginGroup()
    {
        var box = new GroupBox
        {
            Text = "1. 登录公众号后台（可选，只有登录后才能列出全部历史文章）",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 4, 10, 8)
        };

        _lblLogin.Text = "当前状态：未登录";
        _lblLogin.AutoSize = true;
        _lblLogin.Margin = new Padding(3, 8, 16, 3);

        var btn = new Button { Text = "登录…", Width = 90, Height = 27, Margin = new Padding(0, 4, 8, 3) };
        btn.Click += (_, __) => OpenLogin();

        var btnOut = new Button { Text = "清除登录", Width = 90, Height = 27, Margin = new Padding(0, 4, 3, 3) };
        btnOut.Click += (_, __) =>
        {
            _mp.Dispose();
            _mp = new MpSession();
            _fakeId = null;
            _lblLogin.Text = "当前状态：未登录";
            AppendLog("已清除本机登录状态。", LogLevel.Info);
        };

        var row = Row(_lblLogin, btn, btnOut);

        var hint = new Label
        {
            Text = "用任意订阅号扫码登录一次即可（个人号就行，不需要发文）。程序不保存密码，" +
                   "只在本机缓存登录凭证；不登录也能用「链接导入」逐篇抓取。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 2, 3, 2),
            MaximumSize = new Size(1050, 0)
        };

        var inner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, RowCount = 2 };
        inner.Controls.Add(row, 0, 0);
        inner.Controls.Add(hint, 0, 1);
        box.Controls.Add(inner);
        return box;
    }

    private GroupBox BuildFetchGroup()
    {
        var box = new GroupBox
        {
            Text = "2. 拉取文章目录",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 4, 10, 8)
        };

        _txtAccount.Width = 240;
        _txtAccount.Text = _cfg.AccountName;
        _txtAccount.Margin = new Padding(0, 4, 12, 3);

        _dtFrom.Format = DateTimePickerFormat.Short;
        _dtFrom.ShowCheckBox = true;
        _dtFrom.Checked = false;
        _dtFrom.Width = 120;
        _dtFrom.Margin = new Padding(0, 4, 6, 3);

        _dtTo.Format = DateTimePickerFormat.Short;
        _dtTo.ShowCheckBox = true;
        _dtTo.Checked = false;
        _dtTo.Value = DateTime.Today;
        _dtTo.Width = 120;
        _dtTo.Margin = new Padding(0, 4, 12, 3);

        var row1 = Row(
            new Label { Text = "公众号名称：", AutoSize = true, Margin = new Padding(3, 8, 0, 3) },
            _txtAccount,
            new Label { Text = "（留空则先按名称定位）", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 8, 3, 3) });

        var row2 = Row(
            new Label { Text = "日期范围：", AutoSize = true, Margin = new Padding(3, 8, 0, 3) },
            _dtFrom,
            new Label { Text = "至", AutoSize = true, Margin = new Padding(0, 8, 0, 3) },
            _dtTo,
            new Label { Text = "（不勾选＝全部历史）", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 8, 3, 3) });

        _btnFetchOnly.Text = "只拉取目录";
        _btnFetchOnly.Width = 110;
        _btnFetchOnly.Height = 27;
        _btnFetchOnly.Margin = new Padding(0, 4, 8, 3);
        _btnFetchOnly.Click += async (_, __) => await StartFetch(false);

        _btnFetchDownload.Text = "拉取并下载";
        _btnFetchDownload.Width = 110;
        _btnFetchDownload.Height = 27;
        _btnFetchDownload.Margin = new Padding(0, 4, 8, 3);
        _btnFetchDownload.Click += async (_, __) => await StartFetch(true);

        _btnStop.Text = "停止";
        _btnStop.Width = 70;
        _btnStop.Height = 27;
        _btnStop.Enabled = false;
        _btnStop.Margin = new Padding(0, 4, 3, 3);
        _btnStop.Click += (_, __) => StopWork();

        var row3 = Row(_btnFetchOnly, _btnFetchDownload, _btnStop);

        var inner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, RowCount = 3 };
        inner.Controls.Add(row1, 0, 0);
        inner.Controls.Add(row2, 0, 1);
        inner.Controls.Add(row3, 0, 2);
        box.Controls.Add(inner);
        return box;
    }

    private GroupBox BuildListGroup()
    {
        var box = new GroupBox { Text = "3. 文章列表", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };

        var top = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(0, 4, 0, 2) };
        _txtSearch.Width = 240;
        _txtSearch.Margin = new Padding(0, 3, 12, 3);
        _txtSearch.TextChanged += (_, __) => RefreshList();

        _cboStatus.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboStatus.Width = 110;
        _cboStatus.Items.AddRange(new object[] { "全部", "已归档", "待抓取", "失败 / 失效" });
        _cboStatus.SelectedIndex = 0;
        _cboStatus.Margin = new Padding(0, 3, 12, 3);
        _cboStatus.SelectedIndexChanged += (_, __) => RefreshList();

        _lblListCount.Text = "";
        _lblListCount.AutoSize = true;
        _lblListCount.ForeColor = SystemColors.GrayText;
        _lblListCount.Margin = new Padding(0, 8, 3, 3);

        var searchRow = Row(
            new Label { Text = "搜索：", AutoSize = true, Margin = new Padding(3, 7, 0, 3) },
            _txtSearch,
            new Label { Text = "状态：", AutoSize = true, Margin = new Padding(0, 7, 0, 3) },
            _cboStatus,
            _lblListCount);
        searchRow.Dock = DockStyle.Fill;
        top.Controls.Add(searchRow);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(0, 6, 0, 0) };
        var b1 = new Button { Text = "全选", Width = 70, Height = 27, Margin = new Padding(3, 0, 6, 0) };
        b1.Click += (_, __) => SetAllChecked(true);
        var b2 = new Button { Text = "反选", Width = 70, Height = 27, Margin = new Padding(0, 0, 6, 0) };
        b2.Click += (_, __) => SetAllChecked(false, invert: true);

        _btnDownloadChecked.Text = "下载勾选的文章";
        _btnDownloadChecked.Width = 150;
        _btnDownloadChecked.Height = 27;
        _btnDownloadChecked.Margin = new Padding(0, 0, 6, 0);
        _btnDownloadChecked.Click += async (_, __) => await DownloadChecked();

        var open = new Button { Text = "打开归档目录", Width = 110, Height = 27, Margin = new Padding(0, 0, 6, 0) };
        open.Click += (_, __) => OpenOutput();
        var pdf = new Button { Text = "导出 PDF", Width = 90, Height = 27, Margin = new Padding(0, 0, 6, 0) };
        pdf.Click += async (_, __) => await ExportPdf();
        var zip = new Button { Text = "打包 ZIP", Width = 90, Height = 27, Margin = new Padding(0, 0, 6, 0) };
        zip.Click += (_, __) => ExportZip();
        var del = new Button { Text = "移除记录", Width = 90, Height = 27, Margin = new Padding(0, 0, 6, 0) };
        del.Click += (_, __) => RemoveCheckedRecords();

        var actions = Row(b1, b2, _btnDownloadChecked, open, pdf, zip, del);
        actions.Dock = DockStyle.Fill;
        bottom.Controls.Add(actions);

        _lvCatalog.Dock = DockStyle.Fill;
        _lvCatalog.View = View.Details;
        _lvCatalog.CheckBoxes = true;
        _lvCatalog.FullRowSelect = true;
        _lvCatalog.GridLines = true;
        _lvCatalog.HideSelection = false;
        _lvCatalog.MultiSelect = true;
        _lvCatalog.Columns.Add("日期", 100);
        _lvCatalog.Columns.Add("标题", 560);
        _lvCatalog.Columns.Add("状态", 90);
        _lvCatalog.Columns.Add("图片", 60, HorizontalAlignment.Right);
        _lvCatalog.Columns.Add("字数", 70, HorizontalAlignment.Right);
        _lvCatalog.Columns.Add("来源", 110);
        _lvCatalog.ItemChecked += (_, e) =>
        {
            if (_refreshing || e.Item.Tag is not ArticleRecord r) return;
            if (e.Item.Checked) _checkedIds.Add(r.Id); else _checkedIds.Remove(r.Id);
            UpdateDownloadButton();
        };
        _lvCatalog.ItemActivate += (_, __) => OpenSelectedArticle();
        _lvCatalog.Resize += (_, __) => FitTitleColumn();

        box.Controls.Add(_lvCatalog);
        box.Controls.Add(bottom);
        box.Controls.Add(top);
        return box;
    }

    // ---------------- 链接页 ----------------

    private TabPage BuildLinksPage()
    {
        var page = new TabPage("链接导入") { Padding = new Padding(6) };

        var hint = new Label
        {
            Text = "在微信里打开文章 → 右上角「…」→ 复制链接，粘到下面的框里（一行一条）。" +
                   "也可以直接粘一整段聊天记录，程序会自动挑出其中的链接。",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 40,
            Padding = new Padding(4, 8, 4, 0)
        };

        _txtLinks.Dock = DockStyle.Fill;
        _txtLinks.Multiline = true;
        _txtLinks.AcceptsReturn = true;
        _txtLinks.ScrollBars = ScrollBars.Both;
        _txtLinks.WordWrap = false;
        _txtLinks.Font = new Font("Consolas", 9f);
        _txtLinks.TextChanged += (_, __) => UpdateLinkCount();

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(0, 7, 0, 0) };
        var paste = new Button { Text = "粘贴剪贴板", Width = 110, Height = 27, Margin = new Padding(3, 0, 6, 0) };
        paste.Click += (_, __) => PasteFromClipboard();
        var import = new Button { Text = "从文件导入…", Width = 110, Height = 27, Margin = new Padding(0, 0, 6, 0) };
        import.Click += (_, __) => ImportFromFile();
        var clear = new Button { Text = "清空", Width = 70, Height = 27, Margin = new Padding(0, 0, 6, 0) };
        clear.Click += (_, __) => { _txtLinks.Clear(); UpdateLinkCount(); };
        var start = new Button { Text = "开始抓取", Width = 110, Height = 27, Margin = new Padding(0, 0, 12, 0) };
        start.Click += async (_, __) => await StartLinkCrawl();
        _lblLinkCount.Text = "已识别 0 条链接";
        _lblLinkCount.AutoSize = true;
        _lblLinkCount.ForeColor = SystemColors.GrayText;
        _lblLinkCount.Margin = new Padding(0, 8, 3, 3);

        var row = Row(paste, import, clear, start, _lblLinkCount);
        row.Dock = DockStyle.Fill;
        bottom.Controls.Add(row);

        page.Controls.Add(_txtLinks);
        page.Controls.Add(bottom);
        page.Controls.Add(hint);
        return page;
    }

    // ---------------- 设置页 ----------------

    private TabPage BuildSettingsPage()
    {
        var page = new TabPage("设置") { Padding = new Padding(10) };

        var tl = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 10,
            Padding = new Padding(4)
        };
        tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _txtOut.Width = 300;
        var browse = new Button { Text = "浏览…", Width = 80, Height = 25 };

        AddRow(tl, 0, "归档目录：", _txtOut, browse);
        AddRow(tl, 1, "公众号名称：", _txtAccountCfg = new TextBox { Width = 300 }, null);
        AddRow(tl, 2, "每篇抓取间隔（毫秒）：", _txtImgDelay = new TextBox { Width = 100 }, null);
        AddRow(tl, 3, "目录每页条数：", _txtPageSize = new TextBox { Width = 100 }, null);
        AddRow(tl, 4, "翻页间隔（秒）：", _txtDelay = new TextBox { Width = 100 }, null);

        _chkImages.Text = "保存图片";
        _chkImages.AutoSize = true;
        _chkAudio.Text = "保存语音";
        _chkAudio.AutoSize = true;
        _chkMd.Text = "生成 Markdown";
        _chkMd.AutoSize = true;
        var chkRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        chkRow.Controls.AddRange(new Control[] { _chkImages, _chkAudio, _chkMd });
        tl.Controls.Add(new Label { Text = "归档内容：", AutoSize = true, Margin = new Padding(3, 8, 0, 3) }, 0, 5);
        tl.Controls.Add(chkRow, 1, 5);
        tl.SetColumnSpan(chkRow, 2);

        var save = new Button { Text = "保存设置", Width = 110, Height = 28, Margin = new Padding(3, 10, 0, 0) };
        save.Click += (_, __) => SaveSettings();
        var saveRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        saveRow.Controls.Add(save);
        tl.Controls.Add(saveRow, 1, 6);

        browse.Click += (_, __) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "选择归档目录", SelectedPath = _txtOut.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK) _txtOut.Text = dlg.SelectedPath;
        };

        page.Controls.Add(tl);
        return page;

        static void AddRow(TableLayoutPanel tl, int row, string label, Control c, Control extra)
        {
            tl.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 0, 3) }, 0, row);
            tl.Controls.Add(c, 1, row);
            if (extra != null) tl.Controls.Add(extra, 2, row);
        }
    }

    private GroupBox BuildLogPanel()
    {
        var box = new GroupBox { Text = "输出", Dock = DockStyle.Fill, Padding = new Padding(6, 2, 6, 6) };
        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true;
        _log.BackColor = SystemColors.Window;
        _log.BorderStyle = BorderStyle.None;
        _log.Font = new Font("Consolas", 9f);
        _log.WordWrap = true;
        _log.ScrollBars = RichTextBoxScrollBars.Vertical;
        box.Controls.Add(_log);
        return box;
    }

    private static FlowLayoutPanel Row(params Control[] items)
    {
        var f = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        f.Controls.AddRange(items);
        return f;
    }

    // =====================================================================
    //  生命周期
    // =====================================================================

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try { _split.SplitterDistance = Math.Max(320, _split.Height - 190); } catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
        base.OnFormClosing(e);
    }

    // =====================================================================
    //  登录 / 目录
    // =====================================================================

    private void OpenLogin()
    {
        if (_busy) { Info("有任务正在进行，请先点「停止」。"); return; }
        using var f = new LoginForm(_mp, _cfg);
        if (f.ShowDialog(this) == DialogResult.OK && _mp.HasSession)
        {
            _lblLogin.Text = "当前状态：已登录";
            AppendLog("登录成功，已取得后台查询凭证。", LogLevel.Ok);
            _ = LocateAsync(true);
        }
    }

    private async Task LocateAsync(bool silent)
    {
        var kw = (_txtAccount.Text ?? "").Trim();
        if (kw.Length == 0) return;
        try
        {
            _lblLogin.Text = "当前状态：已登录，正在定位公众号…";
            var hits = await _mp.SearchAccountAsync(kw, CancellationToken.None);
            if (hits.Count == 0)
            {
                AppendLog($"没搜到公众号「{kw}」，请核对名称。", LogLevel.Warn);
                _lblLogin.Text = "当前状态：已登录（未定位到公众号）";
                return;
            }

            var hit = hits.FirstOrDefault(h => h.NickName == kw)
                      ?? hits.FirstOrDefault(h => h.NickName.Contains(kw) || kw.Contains(h.NickName))
                      ?? hits[0];

            _fakeId = hit.FakeId;
            _cfg.FakeId = _fakeId;
            _cfg.AccountName = hit.NickName;
            _cfg.Save();

            _txtAccount.Text = hit.NickName;
            Text = "公众号文章采集器 - " + hit.NickName;
            _lblLogin.Text = "当前状态：已登录 · 目标：" + hit.NickName;
            AppendLog($"已定位目标公众号：{hit.NickName}", LogLevel.Ok);
        }
        catch (Exception ex)
        {
            AppendLog("定位失败：" + ex.Message, LogLevel.Error);
            _lblLogin.Text = "当前状态：已登录（定位失败）";
        }
    }

    private async Task StartFetch(bool autoDownload)
    {
        if (_busy) { Info("有任务正在进行，请先点「停止」。"); return; }
        if (!_mp.HasSession)
        {
            if (MessageBox.Show(this, "拉取整份目录需要先登录一次公众号后台（任意订阅号即可）。\r\n\r\n现在去登录？",
                    "需要登录", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            OpenLogin();
            if (!_mp.HasSession) return;
        }

        DateTime? from = _dtFrom.Checked ? _dtFrom.Value.Date : null;
        DateTime? to = _dtTo.Checked ? _dtTo.Value.Date.AddDays(1).AddSeconds(-1) : null;
        await FetchCatalogAsync(from, to, autoDownload);
    }

    private async Task FetchCatalogAsync(DateTime? from, DateTime? to, bool autoDownload)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);
        int pages = 0, added = 0;

        try
        {
            await LocateAsync(true);
            if (string.IsNullOrEmpty(_fakeId)) { Err("没有定位到公众号，无法拉取目录。"); return; }

            var pageSize = Math.Clamp(_cfg.PageSize, 5, 20);
            AppendLog($"开始拉取目录：每页 {pageSize} 条" +
                      (from.HasValue || to.HasValue
                          ? $"，日期范围 {(from?.ToString("yyyy-MM-dd") ?? "最早")} ~ {(to?.ToString("yyyy-MM-dd") ?? "最新")}"
                          : "，全部历史"), LogLevel.Info);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await FetchWithRetryAsync(pageSize, ct);
                if (page.Total > 0) _total = page.Total;
                if (page.Items.Count == 0) { AppendLog("返回空页，目录已到底。", LogLevel.Ok); break; }

                added += _lib.Merge(page.Items);
                pages++;
                _nextBegin += pageSize;

                AppendLog($"第 {pages} 页：{page.Items.Count} 篇，新增 {added} 累计，索引共 {_lib.Items.Count} 篇" +
                          (_total > 0 ? $" / 约 {_total} 篇" : ""), LogLevel.Info);

                RefreshList();
                UpdateCounts();
                if (_total > 0) SetProgress(Math.Min(99, _nextBegin * 100.0 / _total), $"目录 {_nextBegin}/{_total}");
                else SetProgress(Math.Min(95, pages * 4), $"已拉取 {_lib.Items.Count} 篇");

                // 列表按时间倒序：翻到早于起始日期就可以收手
                if (from.HasValue)
                {
                    var oldest = page.Items.Where(x => x.PublishAt > 0).Select(x => x.PublishAt).DefaultIfEmpty(long.MaxValue).Min();
                    if (oldest < Unix(from.Value)) { AppendLog("已翻到早于起始日期，停止翻页。", LogLevel.Info); break; }
                }
                if (_total > 0 && _nextBegin >= _total) { AppendLog("目录拉取完成。", LogLevel.Ok); break; }
                if (pages >= 800) { AppendLog("已达页数上限，停止。", LogLevel.Warn); break; }

                var wait = Random.Shared.Next(Math.Max(2000, _cfg.DelayMinMs), Math.Max(4000, _cfg.DelayMaxMs));
                SetProgress(_progress.Value, $"等待 {wait / 1000.0:0.#} 秒后翻下一页…");
                await Task.Delay(wait, ct);
            }

            _nextBegin = 0;
            _lib.Save();
            SetProgress(100, "目录完成");
            AppendLog($"目录完成：索引共 {_lib.Items.Count} 篇（本次新增 {added}）。", LogLevel.Ok);
        }
        catch (OperationCanceledException) { AppendLog("已停止拉取。", LogLevel.Warn); }
        catch (MpException ex)
        {
            AppendLog("拉取中断：" + ex.Message, LogLevel.Error);
            if (ex.Kind == MpError.FreqControl)
                AppendLog("被微信限流：等几分钟再点「拉取文章目录」，会从当前页继续。", LogLevel.Warn);
            if (ex.Kind == MpError.LoginExpired)
                AppendLog("登录已过期：请重新点「登录…」扫码后再拉。", LogLevel.Warn);
        }
        catch (Exception ex) { AppendLog("拉取中断：" + ex.Message, LogLevel.Error); }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }

        if (autoDownload)
        {
            var targets = _lib.Items
                .Where(x => x.Status != ArticleStatus.Done)
                .Where(x => InRange(x, from, to))
                .ToList();
            if (targets.Count == 0) { AppendLog("这个范围内没有需要下载的文章。", LogLevel.Info); return; }
            if (targets.Count > 30 &&
                MessageBox.Show(this, $"将要下载 {targets.Count} 篇文章（含图片），可能耗时较长。继续吗？",
                    "确认下载", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            await DownloadBatchAsync(targets);
        }
    }

    private async Task<CatalogPage> FetchWithRetryAsync(int pageSize, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            try { return await _mp.FetchPageAsync(_fakeId, _nextBegin, pageSize, ct); }
            catch (MpException ex) when (ex.Kind == MpError.FreqControl && attempt < Math.Max(1, _cfg.MaxRetry))
            {
                attempt++;
                var wait = 40 * attempt;
                AppendLog($"被限流，{wait} 秒后自动重试（{attempt}/{_cfg.MaxRetry}）…", LogLevel.Warn);
                await Task.Delay(wait * 1000, ct);
            }
        }
    }

    private static long Unix(DateTime d) => new DateTimeOffset(d).ToUnixTimeSeconds();

    private static bool InRange(ArticleRecord r, DateTime? from, DateTime? to)
    {
        if (!from.HasValue && !to.HasValue) return true;
        if (r.PublishAt <= 0) return !from.HasValue && !to.HasValue;
        var t = DateTimeOffset.FromUnixTimeSeconds(r.PublishAt).ToLocalTime().DateTime;
        if (from.HasValue && t < from.Value.Date) return false;
        if (to.HasValue && t > to.Value) return false;
        return true;
    }

    // =====================================================================
    //  下载
    // =====================================================================

    private async Task DownloadChecked()
    {
        if (_busy) { Info("有任务正在进行。"); return; }
        var targets = _lib.Items.Where(x => _checkedIds.Contains(x.Id) && x.Status != ArticleStatus.Done).ToList();
        if (targets.Count == 0)
        {
            Info("请先在列表里勾选要下载的文章（或点「全选」）。");
            return;
        }
        await DownloadBatchAsync(targets);
    }

    private async Task StartLinkCrawl()
    {
        if (_busy) { Info("有任务正在进行。"); return; }
        var links = LinkParser.Extract(_txtLinks.Text);
        if (links.Count == 0)
        {
            Info("没有识别到公众号文章链接。请先在微信里「复制链接」再粘贴进来。");
            return;
        }

        int added = 0;
        foreach (var link in links)
        {
            var (rec, isNew) = _lib.Upsert(link, LinkParser.ShortTitle(link), "", "", 0);
            if (isNew) { rec.Source = "链接导入"; added++; }
        }
        _lib.Save();
        AppendLog(added > 0 ? $"新收录 {added} 条链接（共识别 {links.Count} 条）。" : "这些链接都已经在库里了。", LogLevel.Info);
        RefreshList();

        var set = new HashSet<string>(links);
        var targets = _lib.Items.Where(x => set.Contains(x.Link) && x.Status != ArticleStatus.Done).ToList();
        if (targets.Count == 0) { AppendLog("这些链接都已归档，无需重复下载。", LogLevel.Info); return; }
        await DownloadBatchAsync(targets);
    }

    private async Task DownloadBatchAsync(List<ArticleRecord> list)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);
        int ok = 0, fail = 0, gone = 0;

        try
        {
            AppendLog($"开始下载 {list.Count} 篇…", LogLevel.Info);
            for (int i = 0; i < list.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var rec = list[i];
                SetProgress(i * 100.0 / list.Count, $"[{i + 1}/{list.Count}] {Cut(rec.Title, 26)}");

                try
                {
                    var res = await _dl.DownloadAsync(rec, _lib.Root, _cfg, _cfg.AccountName, m => AppendLog(m), ct);
                    rec.Status = ArticleStatus.Done;
                    rec.ImageCount = res.Images;
                    rec.WordCount = res.Words;
                    rec.Error = "";
                    if (!string.IsNullOrWhiteSpace(res.Title)) rec.Title = res.Title;
                    if (res.PublishAt > 0) rec.PublishAt = res.PublishAt;
                    rec.Text = res.PlainText;
                    if (!string.IsNullOrWhiteSpace(res.Author)) rec.Digest = res.Author;
                    rec.UpdatedAt = DateTime.Now;
                    ok++;
                    AppendLog($"√ {Cut(rec.Title, 30)}（{res.Images} 图 / {res.Words} 字）", LogLevel.Ok);
                }
                catch (ArticleUnavailableException ex)
                {
                    rec.Status = ArticleStatus.Unavailable; rec.Error = ex.Message; gone++;
                    AppendLog($"○ 已失效：{Cut(rec.Title, 24)} — {ex.Message}", LogLevel.Warn);
                }
                catch (ArticleBlockedException ex)
                {
                    rec.Status = ArticleStatus.Failed; rec.Error = ex.Message; fail++;
                    AppendLog($"× 失败：{Cut(rec.Title, 24)} — {ex.Message}", LogLevel.Error);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    rec.Status = ArticleStatus.Failed; rec.Error = ex.Message; fail++;
                    AppendLog($"× 异常：{Cut(rec.Title, 24)} — {ex.Message}", LogLevel.Error);
                }

                rec.UpdatedAt = DateTime.Now;
                _lib.Save();
                UpdateRow(rec);
                UpdateCounts();

                await Task.Delay(Math.Max(120, _cfg.ImageDelayMs * 2) + Random.Shared.Next(0, 240), ct);
            }

            SetProgress(100, "完成");
            AppendLog($"下载结束：成功 {ok} 篇" + (fail > 0 ? $"，失败 {fail} 篇" : "") +
                      (gone > 0 ? $"，失效 {gone} 篇" : "") + "。", LogLevel.Ok);
        }
        catch (OperationCanceledException) { AppendLog("已停止下载。", LogLevel.Warn); }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
            RefreshList();
            UpdateCounts();
        }
    }

    private void StopWork()
    {
        try
        {
            _cts?.Cancel();
            AppendLog("正在停止…", LogLevel.Warn);
        }
        catch { }
    }

    // =====================================================================
    //  列表
    // =====================================================================

    private IEnumerable<ArticleRecord> FilteredItems()
    {
        var q = (_txtSearch.Text ?? "").Trim();
        var status = _cboStatus.SelectedIndex;

        foreach (var r in _lib.Items)
        {
            if (q.Length > 0 &&
                !r.Title.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !r.Digest.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !(r.Text ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)) continue;

            if (status == 1 && r.Status != ArticleStatus.Done) continue;
            if (status == 2 && r.Status != ArticleStatus.Pending) continue;
            if (status == 3 && r.Status is not (ArticleStatus.Failed or ArticleStatus.Unavailable)) continue;

            yield return r;
        }
    }

    private void RefreshList()
    {
        if (_lvCatalog == null) return;
        _refreshing = true;
        _lvCatalog.BeginUpdate();
        try
        {
            _lvCatalog.Items.Clear();
            _map.Clear();
            foreach (var r in FilteredItems())
            {
                var it = new ListViewItem(r.DateText) { Tag = r, Checked = _checkedIds.Contains(r.Id) };
                it.SubItems.Add(r.Title);
                it.SubItems.Add(r.StatusText);
                it.SubItems.Add(r.ImageCount > 0 ? r.ImageCount.ToString() : "");
                it.SubItems.Add(r.WordCount > 0 ? r.WordCount.ToString() : "");
                it.SubItems.Add(r.Source ?? "");

                it.ForeColor = r.Status switch
                {
                    ArticleStatus.Done => Color.FromArgb(0x1B, 0x6E, 0x45),
                    ArticleStatus.Failed => Color.FromArgb(0xB0, 0x28, 0x28),
                    ArticleStatus.Unavailable => Color.FromArgb(0x8A, 0x5A, 0x00),
                    _ => SystemColors.WindowText
                };

                _lvCatalog.Items.Add(it);
                _map[r.Id] = it;
            }
        }
        finally
        {
            _lvCatalog.EndUpdate();
            _refreshing = false;
        }
        FitTitleColumn();
        UpdateDownloadButton();
        if (_lblListCount != null) _lblListCount.Text = $"共 {_lvCatalog.Items.Count} 条";
    }

    private void UpdateRow(ArticleRecord r)
    {
        if (!_map.TryGetValue(r.Id, out var it)) { RefreshList(); return; }
        it.SubItems[1].Text = r.Title;
        it.SubItems[2].Text = r.StatusText;
        it.SubItems[3].Text = r.ImageCount > 0 ? r.ImageCount.ToString() : "";
        it.SubItems[4].Text = r.WordCount > 0 ? r.WordCount.ToString() : "";
    }

    private void FitTitleColumn()
    {
        try
        {
            if (_lvCatalog.Columns.Count < 6) return;
            var other = 0;
            for (int i = 0; i < _lvCatalog.Columns.Count; i++)
                if (i != 1) other += _lvCatalog.Columns[i].Width;
            var w = _lvCatalog.ClientSize.Width - other - 4;
            _lvCatalog.Columns[1].Width = Math.Max(200, w);
        }
        catch { }
    }

    private void SetAllChecked(bool value, bool invert = false)
    {
        _refreshing = true;
        try
        {
            foreach (ListViewItem it in _lvCatalog.Items)
            {
                it.Checked = invert ? !it.Checked : value;
            }
        }
        finally { _refreshing = false; }

        _checkedIds.Clear();
        foreach (ListViewItem it in _lvCatalog.CheckedItems)
            if (it.Tag is ArticleRecord r) _checkedIds.Add(r.Id);

        UpdateDownloadButton();
    }

    private void UpdateDownloadButton()
    {
        if (_btnDownloadChecked == null) return;
        var n = _lvCatalog.CheckedItems.Count;
        _btnDownloadChecked.Text = n > 0 ? $"下载勾选的文章（{n}）" : "下载勾选的文章";
        _btnDownloadChecked.Enabled = n > 0 && !_busy;
    }

    private void OpenSelectedArticle()
    {
        if (_lvCatalog.SelectedItems.Count == 0) return;
        if (_lvCatalog.SelectedItems[0].Tag is not ArticleRecord r) return;
        if (r.Status == ArticleStatus.Done && !string.IsNullOrEmpty(r.Folder))
        {
            var p = Path.Combine(_lib.Root, "articles", r.Folder, "index.html");
            if (File.Exists(p)) { Shell(p); return; }
        }
        try { Shell(r.Link); } catch { }
    }

    // =====================================================================
    //  链接页
    // =====================================================================

    private void UpdateLinkCount()
    {
        var n = LinkParser.Extract(_txtLinks.Text).Count;
        _lblLinkCount.Text = n == 0 ? "已识别 0 条链接" : $"已识别 {n} 条链接";
    }

    private void PasteFromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText()) { Info("剪贴板里没有文本。"); return; }
            var links = LinkParser.Extract(Clipboard.GetText());
            if (links.Count == 0) { Info("剪贴板内容里没有公众号文章链接（应形如 mp.weixin.qq.com/s?...）。"); return; }
            AppendLinks(links);
            AppendLog($"从剪贴板识别到 {links.Count} 条链接。", LogLevel.Ok);
        }
        catch (Exception ex) { AppendLog("读取剪贴板失败：" + ex.Message, LogLevel.Error); }
    }

    private void ImportFromFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择存放链接的文本文件",
            Filter = "文本文件|*.txt;*.csv;*.md;*.log|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var links = LinkParser.Extract(File.ReadAllText(dlg.FileName));
            if (links.Count == 0) { Info("文件里没有识别到链接。"); return; }
            AppendLinks(links);
            AppendLog($"从文件导入 {links.Count} 条链接。", LogLevel.Ok);
        }
        catch (Exception ex) { AppendLog("读取文件失败：" + ex.Message, LogLevel.Error); }
    }

    private void AppendLinks(List<string> links)
    {
        var existing = new HashSet<string>(LinkParser.Extract(_txtLinks.Text));
        var sb = new StringBuilder(_txtLinks.Text.TrimEnd());
        foreach (var l in links)
        {
            if (!existing.Add(l)) continue;
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(l);
        }
        _txtLinks.Text = sb.ToString();
        _txtLinks.SelectionStart = _txtLinks.TextLength;
        _txtLinks.ScrollToCaret();
        UpdateLinkCount();
    }

    // =====================================================================
    //  导出 / 文件
    // =====================================================================

    private async Task ExportPdf()
    {
        if (_busy) { Info("有任务正在进行。"); return; }
        var sel = SelectedOrChecked().Where(x => x.Status == ArticleStatus.Done && !string.IsNullOrEmpty(x.Folder)).ToList();
        if (sel.Count == 0) { Info("请先勾选已归档的文章。"); return; }

        SetBusy(true);
        int ok = 0;
        try
        {
            var profile = Path.Combine(_cfg.OutputRoot, "_webview2_print");
            for (int i = 0; i < sel.Count; i++)
            {
                var rec = sel[i];
                SetProgress(i * 100.0 / sel.Count, $"PDF {i + 1}/{sel.Count}");
                var dir = Path.Combine(_lib.Root, "articles", rec.Folder);
                var html = Path.Combine(dir, "index.html");
                var pdf = Path.Combine(dir, AppSettings.SafeName(rec.Title) + ".pdf");
                var done = await PdfExporter.ExportAsync(html, pdf, profile);
                if (done) { ok++; AppendLog("PDF 已生成：" + Cut(rec.Title, 28), LogLevel.Ok); }
                else AppendLog("PDF 生成失败：" + Cut(rec.Title, 28), LogLevel.Warn);
            }
            SetProgress(100, "PDF 完成");
        }
        finally { SetBusy(false); }
    }

    private void ExportZip()
    {
        if (_lib.CountDone == 0) { Info("还没有已归档的文章可以打包。"); return; }
        using var dlg = new SaveFileDialog
        {
            Title = "打包归档目录",
            Filter = "ZIP 压缩包|*.zip",
            FileName = AppSettings.SafeName(_cfg.AccountName) + "_" + DateTime.Now.ToString("yyyyMMdd") + ".zip"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            SetBusy(true);
            SetProgress(0, "正在打包…");
            ZipFile.CreateFromDirectory(_lib.Root, dlg.FileName, CompressionLevel.Optimal, true);
            SetProgress(100, "打包完成");
            AppendLog("已导出：" + dlg.FileName, LogLevel.Ok);
            Shell(Path.GetDirectoryName(dlg.FileName));
        }
        catch (Exception ex) { AppendLog("打包失败：" + ex.Message, LogLevel.Error); }
        finally { SetBusy(false); }
    }

    private List<ArticleRecord> SelectedOrChecked()
    {
        if (_lvCatalog.CheckedItems.Count > 0)
            return _lvCatalog.CheckedItems.Cast<ListViewItem>()
                .Select(i => i.Tag as ArticleRecord).Where(r => r != null).ToList();
        return _lvCatalog.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag as ArticleRecord).Where(r => r != null).ToList();
    }

    private void RemoveCheckedRecords()
    {
        var sel = SelectedOrChecked();
        if (sel.Count == 0) { Info("请先勾选要移除的记录。"); return; }
        if (MessageBox.Show(this, $"仅从列表里移除 {sel.Count} 条记录，已归档的文件不会被删除。继续吗？",
                "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        foreach (var r in sel) { _lib.Items.Remove(r); _checkedIds.Remove(r.Id); }
        _lib.Save();
        RefreshList();
        UpdateCounts();
        AppendLog($"已移除 {sel.Count} 条记录。", LogLevel.Info);
    }

    private void OpenOutput()
    {
        try
        {
            Directory.CreateDirectory(_lib.Root);
            Shell(_lib.Root);
        }
        catch (Exception ex) { AppendLog("打开目录失败：" + ex.Message, LogLevel.Error); }
    }

    // =====================================================================
    //  设置
    // =====================================================================

    private void LoadSettingsToUi()
    {
        _txtOut.Text = _cfg.OutputRoot;
        _txtAccountCfg.Text = _cfg.AccountName;
        _txtImgDelay.Text = _cfg.ImageDelayMs.ToString();
        _txtPageSize.Text = _cfg.PageSize.ToString();
        _txtDelay.Text = (_cfg.DelayMinMs / 1000).ToString();
        _chkImages.Checked = _cfg.DownloadImages;
        _chkAudio.Checked = _cfg.DownloadAudio;
        _chkMd.Checked = _cfg.ExportMarkdown;
    }

    private void SaveSettings()
    {
        var oldRoot = _cfg.OutputRoot;
        if (!string.IsNullOrWhiteSpace(_txtOut.Text)) _cfg.OutputRoot = _txtOut.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_txtAccountCfg.Text)) _cfg.AccountName = _txtAccountCfg.Text.Trim();
        _cfg.ImageDelayMs = ParseInt(_txtImgDelay.Text, _cfg.ImageDelayMs, 0, 5000);
        _cfg.PageSize = ParseInt(_txtPageSize.Text, _cfg.PageSize, 5, 20);
        _cfg.DelayMinMs = ParseInt(_txtDelay.Text, _cfg.DelayMinMs / 1000, 2, 120) * 1000;
        _cfg.DelayMaxMs = Math.Max(_cfg.DelayMinMs + 2000, _cfg.DelayMaxMs);
        _cfg.DownloadImages = _chkImages.Checked;
        _cfg.DownloadAudio = _chkAudio.Checked;
        _cfg.ExportMarkdown = _chkMd.Checked;
        _cfg.Save();

        _txtAccount.Text = _cfg.AccountName;
        LoadSettingsToUi();

        if (oldRoot != _cfg.OutputRoot)
        {
            _lib.Rebind(_cfg.AccountDir);
            _checkedIds.Clear();
            RefreshList();
            UpdateCounts();
            AppendLog("归档目录已切换：" + _cfg.AccountDir, LogLevel.Info);
        }
        AppendLog("设置已保存。", LogLevel.Ok);
    }

    // =====================================================================
    //  辅助
    // =====================================================================

    private void AppendLog(string text, LogLevel level = LogLevel.Info)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => AppendLog(text, level))); return; }

        var line = new LogItem { Time = DateTime.Now.ToString("HH:mm:ss"), Text = text, Level = level };
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = Color.Gray;
        _log.AppendText(line.Time + "  ");
        _log.SelectionColor = line.Color;
        _log.AppendText(text + Environment.NewLine);
        _log.SelectionColor = _log.ForeColor;
        _log.ScrollToCaret();

        if (_log.TextLength > 200000) _log.Clear();

        if (_lblStatus != null && level is LogLevel.Info or LogLevel.Ok)
            _lblStatus.Text = Cut(text, 80);
    }

    private void UpdateCounts()
    {
        if (_lblCounts == null) return;
        _lblCounts.Text = $"共 {_lib.Items.Count} 篇 · 已归档 {_lib.CountDone} · 待抓取 {_lib.CountPending} · 失败 {_lib.CountFailed}";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _btnFetchOnly.Enabled = !busy;
        _btnFetchDownload.Enabled = !busy;
        _btnStop.Enabled = busy;
        _btnStop.Enabled = busy;
        if (!busy) SetProgress(100, "就绪");
        UpdateDownloadButton();
        Cursor = busy ? Cursors.AppStarting : Cursors.Default;
    }

    private void SetProgress(double pct, string status)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetProgress(pct, status))); return; }
        try
        {
            _progress.Value = Math.Max(0, Math.Min(100, (int)pct));
            if (!string.IsNullOrEmpty(status)) _lblStatus.Text = status;
        }
        catch { }
    }

    /// <summary>系统代理会让微信后台接口立刻返回 200013，启动时提示一下。</summary>
    private void WarnIfProxy()
    {
        try
        {
            if (HttpClient.DefaultProxy is WebProxy wp && wp.Address != null)
                AppendLog($"检测到系统代理 {wp.Address}。微信后台接口经代理出口极易被判“操作过于频繁”，" +
                          "程序已改为直连微信（只有直连不通时才回退代理）。", LogLevel.Warn);
        }
        catch { }
    }

    private void Info(string msg) => MessageBox.Show(this, msg, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
    private void Err(string msg) => MessageBox.Show(this, msg, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static string Cut(string s, int len)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= len ? s : s[..len] + "…");

    private static int ParseInt(string s, int fallback, int min, int max)
        => int.TryParse((s ?? "").Trim(), out var v) ? Math.Clamp(v, min, max) : Math.Clamp(fallback, min, max);

    private static void Shell(string path)
        => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private void ShowHelp()
    {
        MessageBox.Show(this,
            "两种用法：\r\n\r\n" +
            "【链接导入】在微信里打开文章 → 右上角「…」→ 复制链接 → 粘到「链接导入」页 → 开始抓取。\r\n" +
            "不需要登录，适合抓零散几篇。\r\n\r\n" +
            "【公众号目录】点「登录…」用任意订阅号扫码登录一次，程序就能翻出该号的全部历史文章目录；" +
            "可以选日期范围后「拉取并下载」，一次性归档某个时间段的所有文章。\r\n\r\n" +
            "微信没有开放免登录的文章列表接口，所以“列出全部文章”这一步只能用登录态换。",
            "使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            "公众号文章采集器 3.0\r\n\r\n" +
            "把公众号文章存成本地可离线阅读的文件：\r\n" +
            "index.html、article.md、meta.json、assets（图片与语音）。\r\n\r\n" +
            "仅供个人学习与家校留档使用，内容版权归原公众号及作者所有。",
            "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
