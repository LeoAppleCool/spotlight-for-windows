using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Spotlight.Core;

namespace Spotlight.Providers;

/// <summary>
/// Documents that contain the query in their text, via the Windows Search index (OLE DB provider used through ADO,
/// so no extra libraries are needed). Runs on one worker thread; only the newest request is executed.
/// </summary>
internal sealed class ContentSearchProvider : IAsyncSearchProvider, IDisposable
{
    private sealed record Request(Query Query, CancellationToken Token, TaskCompletionSource<List<SearchResult>> Result);

    private static readonly string[] NoiseSegments = [@"\node_modules\", @"\AppData\", @"\site-packages\", @"\.git\", @"\obj\", @"\$Recycle.Bin\", @"\scoop\"];

    // Real documents only: HTML/markup files found by content are almost always bundled manuals of tools.
    private static readonly HashSet<string> DocumentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".docm", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".odt", ".ods", ".odp",
        ".rtf", ".txt", ".md", ".csv", ".epub", ".eml", ".msg", ".one", ".pages", ".numbers", ".key",
    };

    private const int MaxDepthBelowProfile = 5;

    private readonly AppConfig _config;
    private readonly BlockingCollection<Request> _queue = new();
    private readonly string _scope;
    private readonly int _profileDepth;
    private dynamic? _connection;
    private bool _unavailable;

    public ContentSearchProvider(AppConfig config)
    {
        _config = config;
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _scope = "file:" + profile.Replace('\\', '/');
        _profileDepth = profile.TrimEnd('\\').Split('\\').Length;
        new Thread(Worker) { IsBackground = true, Name = "WindowsSearch" }.Start();
    }

    public bool Accepts(Query query) =>
        _config.SearchFileContents && !_unavailable && query.Norm.Length >= 3
        && !PathProvider.LooksLikePath(query.Raw) && !WebProvider.LooksLikeUrl(query.Raw.Trim());

    public Task<List<SearchResult>> SearchAsync(Query query, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<List<SearchResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new Request(query, ct, tcs));
        return tcs.Task;
    }

    private void Worker()
    {
        foreach (var request in _queue.GetConsumingEnumerable())
        {
            var current = request;
            while (_queue.TryTake(out var newer)) // skip stale requests
            {
                current.Result.TrySetResult([]);
                current = newer;
            }
            if (current.Token.IsCancellationRequested)
            {
                current.Result.TrySetResult([]);
                continue;
            }
            try { current.Result.TrySetResult(Run(current.Query)); }
            catch (Exception ex)
            {
                Log.Error("Windows Search query failed", ex);
                ReleaseConnection();
                current.Result.TrySetResult([]);
            }
        }
    }

    private List<SearchResult> Run(Query query)
    {
        string? condition = BuildCondition(query.Raw);
        var results = new List<SearchResult>();
        if (condition == null) return results;

        if (_connection == null)
        {
            var type = Type.GetTypeFromProgID("ADODB.Connection");
            if (type == null) { _unavailable = true; return results; }
            _connection = Activator.CreateInstance(type)!;
            _connection.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
        }

        string sql = "SELECT TOP 40 System.ItemPathDisplay FROM SystemIndex " +
                     $"WHERE SCOPE='{_scope.Replace("'", "''")}' AND CONTAINS(*, '{condition}') AND System.Kind = 'document' " +
                     "AND System.FileExtension <> '.html' AND System.FileExtension <> '.htm' AND System.FileExtension <> '.xml' " +
                     "ORDER BY System.Search.Rank DESC";

        dynamic recordset = Activator.CreateInstance(Type.GetTypeFromProgID("ADODB.Recordset")!)!;
        try
        {
            recordset.Open(sql, _connection);
            int rank = 0;
            while (!(bool)recordset.EOF && results.Count < 15)
            {
                string? path = recordset.Fields.Item(0).Value as string;
                recordset.MoveNext();
                rank++;
                if (string.IsNullOrEmpty(path) || IsNoise(path)) continue;
                results.Add(FileResult.Create(path, false, 470 - rank * 2));
            }
            recordset.Close();
        }
        finally
        {
            Marshal.FinalReleaseComObject(recordset);
        }
        return results;
    }

    /// <summary>"quarterly rep" → "\"quarterly*\" AND \"rep*\"" with everything CONTAINS cannot parse removed.</summary>
    private static string? BuildCondition(string raw)
    {
        var terms = new List<string>();
        foreach (var token in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var sb = new StringBuilder();
            foreach (char c in token)
                if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.') sb.Append(c);
            if (sb.Length >= 2) terms.Add($"\"{sb}*\"");
        }
        return terms.Count == 0 ? null : string.Join(" AND ", terms).Replace("'", "''");
    }

    private bool IsNoise(string path)
    {
        string ext = Path.GetExtension(path);
        if (!DocumentTypes.Contains(ext)) return true;
        // Log files contain every word eventually.
        if (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(path).Contains("log", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var s in NoiseSegments)
            if (path.Contains(s, StringComparison.OrdinalIgnoreCase)) return true;
        var segments = path.Split('\\');
        if (segments.Length - _profileDepth - 1 > MaxDepthBelowProfile) return true; // deep inside some tool's folder
        foreach (var segment in segments)
            if (segment.Length > 1 && segment[0] == '.') return true;
        return _config.ExcludedFolderNames.Any(n => path.Contains("\\" + n + "\\", StringComparison.OrdinalIgnoreCase));
    }

    private void ReleaseConnection()
    {
        try { if (_connection != null) Marshal.FinalReleaseComObject(_connection); } catch { }
        _connection = null;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
    }
}
