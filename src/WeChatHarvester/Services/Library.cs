using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatHarvester.Models;

namespace WeChatHarvester.Services;

/// <summary>文章索引：整份 JSON 落盘，千级记录足够快，且方便用户直接查看。</summary>
public class Library
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();
    public string Root { get; private set; }
    public ObservableCollection<ArticleRecord> Items { get; } = new();

    public Library(string accountDir)
    {
        Root = accountDir;
    }

    public void Rebind(string accountDir)
    {
        Items.Clear();
        Root = accountDir;
        Load();
    }

    private string IndexFile => Path.Combine(Root, "index.json");

    public void Load()
    {
        try
        {
            Directory.CreateDirectory(Root);
            if (!File.Exists(IndexFile)) return;
            var list = JsonSerializer.Deserialize<List<ArticleRecord>>(File.ReadAllText(IndexFile), Opt);
            if (list == null) return;
            foreach (var a in list.OrderByDescending(x => x.PublishAt)) Items.Add(a);
        }
        catch (Exception ex)
        {
            // 索引损坏不阻塞使用：备份坏文件，从空索引开始
            try { File.Copy(IndexFile, IndexFile + ".broken." + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
            System.Diagnostics.Debug.WriteLine("索引读取失败: " + ex.Message);
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Root);
                var json = JsonSerializer.Serialize(Items.ToList(), Opt);
                var tmp = IndexFile + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(IndexFile)) File.Delete(IndexFile);
                File.Move(tmp, IndexFile);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("索引写入失败: " + ex.Message);
            }
        }
    }

    public string IdOf(string link)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(link ?? ""));
        return Convert.ToHexString(bytes).ToLowerInvariant().Substring(0, 16);
    }

    /// <summary>按链接去重入列，返回记录与"是否新增"。</summary>
    public (ArticleRecord rec, bool isNew) Upsert(string link, string title, string digest, string cover, long publishAt)
    {
        var id = IdOf(link);
        var exist = Items.FirstOrDefault(x => x.Id == id);
        if (exist != null) return (exist, false);

        var rec = new ArticleRecord
        {
            Id = id,
            Link = link,
            Title = title,
            Digest = digest,
            Cover = cover,
            PublishAt = publishAt,
            Status = ArticleStatus.Pending,
            UpdatedAt = DateTime.Now
        };
        Items.Add(rec);
        return (rec, true);
    }

    /// <summary>把新拉到的目录合并进索引，返回新增条数。</summary>
    public int Merge(IEnumerable<ArticleRecord> fetched)
    {
        int added = 0;
        foreach (var f in fetched)
        {
            var id = IdOf(f.Link);
            if (Items.Any(x => x.Id == id)) continue;
            f.Id = id;
            Items.Add(f);
            added++;
        }
        if (added > 0)
        {
            var ordered = Items.OrderByDescending(x => x.PublishAt).ToList();
            Items.Clear();
            foreach (var o in ordered) Items.Add(o);
        }
        Save();
        return added;
    }

    public int CountDone => Items.Count(x => x.Status == ArticleStatus.Done);
    public int CountFailed => Items.Count(x => x.Status is ArticleStatus.Failed or ArticleStatus.Unavailable);
    public int CountPending => Items.Count(x => x.Status == ArticleStatus.Pending);
}
