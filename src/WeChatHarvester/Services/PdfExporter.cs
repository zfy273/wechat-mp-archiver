using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WeChatHarvester.Services;

/// <summary>用 WebView2 的 PrintToPdf 把离线 HTML 打成 PDF（藏在一个小窗体里渲染，不打扰主界面）。</summary>
public static class PdfExporter
{
    public static async Task<bool> ExportAsync(string htmlPath, string pdfPath, string profileDir)
    {
        if (!File.Exists(htmlPath)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(pdfPath));

        using var host = new Form
        {
            Text = "正在生成 PDF",
            Size = new Size(420, 120),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ControlBox = false
        };

        var tip = new Label
        {
            Text = "正在生成 PDF，请稍候…",
            Dock = DockStyle.Top,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0)
        };
        var wv = new WebView2 { Dock = DockStyle.Fill };
        host.Controls.Add(wv);
        host.Controls.Add(tip);
        host.Show();
        host.BringToFront();
        Application.DoEvents();

        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, profileDir, null);
            await wv.EnsureCoreWebView2Async(env);
            wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            wv.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            wv.CoreWebView2.Settings.IsStatusBarEnabled = false;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNav(object s, CoreWebView2NavigationCompletedEventArgs e)
            {
                wv.CoreWebView2.NavigationCompleted -= OnNav;
                tcs.TrySetResult(e.IsSuccess);
            }
            wv.CoreWebView2.NavigationCompleted += OnNav;
            wv.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);

            var ok = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(25));
            if (!ok) return false;
            await Task.Delay(500); // 等本地图片解码

            var settings = wv.CoreWebView2.Environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.MarginTop = 0.45;
            settings.MarginBottom = 0.45;
            settings.MarginLeft = 0.45;
            settings.MarginRight = 0.45;

            return await wv.CoreWebView2.PrintToPdfAsync(pdfPath, settings);
        }
        catch
        {
            return false;
        }
        finally
        {
            try { host.Close(); } catch { }
        }
    }
}
