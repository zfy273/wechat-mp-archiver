using System.Diagnostics;
using System.Text;
using WeChatHarvester.Forms;

namespace WeChatHarvester;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => ShowError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ShowError(e.ExceptionObject as Exception ?? new Exception("未知错误"));

        // 命令行：单篇链接抓取（自动化用）
        if (args.Length >= 2 && args[0].Equals("--fetch", StringComparison.OrdinalIgnoreCase))
        {
            RunFetch(args[1]);
            return;
        }

        Application.Run(new MainForm());
    }

    private static void ShowError(Exception ex)
    {
        MessageBox.Show(ex.Message, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>命令行单篇抓取，结果写入 logs\fetch.log。</summary>
    private static void RunFetch(string raw)
    {
        var lines = new List<string>();
        string logFile = Path.Combine(AppContext.BaseDirectory, "logs", "fetch.log");
        try
        {
            var parsed = Services.LinkParser.Extract(raw);
            var target = parsed.FirstOrDefault();
            lines.Add("PARSE " + (target ?? "(未识别)") + $"  count={parsed.Count}");
            if (target == null) throw new InvalidOperationException("没有识别到公众号文章链接。");

            var cfg = Models.AppSettings.Load();
            var root = Path.Combine(cfg.OutputRoot, "手动导入");
            Directory.CreateDirectory(root);

            var rec = new Models.ArticleRecord
            {
                Id = "cli" + DateTime.Now.ToString("yyMMddHHmmss"),
                Link = target,
                Title = ""
            };

            var dl = new Services.ArticleDownloader();
            var res = Task.Run(() => dl.DownloadAsync(rec, root, cfg, "手动导入", l => lines.Add("LOG " + l), CancellationToken.None))
                          .GetAwaiter().GetResult();

            lines.Add($"SUCCESS images={res.Images} words={res.Words} title={res.Title}");
            lines.Add("DIR " + Path.Combine(root, "articles", res.Folder));
        }
        catch (Exception ex)
        {
            lines.Add("FAIL " + ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile));
            File.WriteAllLines(logFile, lines, new UTF8Encoding(false));
        }
        catch { }
    }
}
