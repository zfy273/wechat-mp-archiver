using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatHarvester.Models;

public enum ArticleStatus
{
    Pending = 0,
    Done = 1,
    Failed = 2,
    Unavailable = 3
}

public class ArticleRecord : INotifyPropertyChanged
{
    public string Id { get; set; }
    public string Title { get; set; } = "";
    public string Link { get; set; } = "";
    public string Digest { get; set; } = "";
    public string Cover { get; set; } = "";
    public string Author { get; set; } = "";
    public long PublishAt { get; set; }
    public string Folder { get; set; } = "";
    public string Error { get; set; } = "";
    /// <summary>页面里解析出的公众号昵称（用于判断是否属于目标号）。</summary>
    public string Owner { get; set; } = "";
    /// <summary>来源：链接导入 / 目录 / 搜索。</summary>
    public string Source { get; set; } = "";
    /// <summary>正文纯文本（截断），供本地全文搜索。</summary>
    public string Text { get; set; } = "";
    public DateTime UpdatedAt { get; set; }

    private ArticleStatus _status;
    public ArticleStatus Status
    {
        get => _status;
        set { _status = value; Raise(); Raise(nameof(StatusText)); }
    }

    private int _imageCount;
    public int ImageCount { get => _imageCount; set { _imageCount = value; Raise(); } }

    private int _wordCount;
    public int WordCount { get => _wordCount; set { _wordCount = value; Raise(); } }

    [JsonIgnore]
    public string DateText => PublishAt > 0
        ? DateTimeOffset.FromUnixTimeSeconds(PublishAt).ToLocalTime().ToString("yyyy-MM-dd")
        : "—";

    [JsonIgnore] public string StatusText => Status switch
    {
        ArticleStatus.Done => "已归档",
        ArticleStatus.Failed => "失败",
        ArticleStatus.Unavailable => "已失效",
        _ => "待下载"
    };

    [JsonIgnore] public string AssetText => ImageCount > 0 ? ImageCount.ToString() : "—";
    [JsonIgnore] public string WordText => WordCount > 0 ? WordCount.ToString() : "—";

    public event PropertyChangedEventHandler PropertyChanged;
    private void Raise([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class AccountHit
{
    public string FakeId { get; set; } = "";
    public string NickName { get; set; } = "";
    public string Alias { get; set; } = "";
    public string HeadImg { get; set; } = "";
    public string Signature { get; set; } = "";
}

public enum LogLevel { Info, Ok, Warn, Error }

public class LogItem
{
    public string Time { get; set; } = DateTime.Now.ToString("HH:mm:ss");
    public string Text { get; set; } = "";
    public LogLevel Level { get; set; }

    public System.Drawing.Color Color => Level switch
    {
        LogLevel.Ok => System.Drawing.Color.FromArgb(0x1B, 0x7F, 0x4B),
        LogLevel.Warn => System.Drawing.Color.FromArgb(0xA0, 0x60, 0x00),
        LogLevel.Error => System.Drawing.Color.FromArgb(0xC0, 0x28, 0x28),
        _ => System.Drawing.SystemColors.WindowText
    };
}

public class AppSettings
{
    public string AccountName { get; set; } = "上海市张江集团中学";
    public string OutputRoot { get; set; } = DefaultOutputRoot();
    public string FakeId { get; set; } = "";
    public int PageSize { get; set; } = 5;
    public int DelayMinMs { get; set; } = 6000;
    public int DelayMaxMs { get; set; } = 11000;
    public bool DownloadImages { get; set; } = true;
    public bool DownloadAudio { get; set; } = true;
    public bool ExportMarkdown { get; set; } = true;
    public int ImageDelayMs { get; set; } = 120;
    public int MaxRetry { get; set; } = 3;

    private static string DefaultOutputRoot()
    {
        try
        {
            if (Directory.Exists(@"D:\")) return @"D:\公众号归档";
        }
        catch { /* ignore */ }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "公众号归档");
    }

    private static string PathFile => Path.Combine(AppContext.BaseDirectory, "settings.json");

    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(PathFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathFile), Opt) ?? new AppSettings();
        }
        catch { /* ignore */ }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(PathFile, JsonSerializer.Serialize(this, Opt)); }
        catch { /* ignore */ }
    }

    public string AccountDir =>
        Path.Combine(OutputRoot, SafeName(string.IsNullOrWhiteSpace(AccountName) ? "公众号" : AccountName));

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || c == '\\' || c == '/' ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.');
        if (s.Length > 60) s = s.Substring(0, 60);
        return string.IsNullOrWhiteSpace(s) ? "未命名" : s;
    }
}
