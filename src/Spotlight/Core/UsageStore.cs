using System.Text.Json;

namespace Spotlight.Core;

/// <summary>
/// Learns from what the user opens: frequently/recently used items rank higher, and an item chosen for a typed
/// query ("ch" → Chrome) wins that query next time — like Spotlight's learning of "Top Hits".
/// </summary>
internal sealed class UsageStore
{
    private const int MaxPrefixes = 4000;

    private sealed class ItemStat
    {
        public int Count { get; set; }
        public DateTime LastUtc { get; set; }
    }

    private sealed class PrefixStat
    {
        public DateTime LastUtc { get; set; }
        public Dictionary<string, double> Keys { get; set; } = new();
    }

    private sealed class Data
    {
        public Dictionary<string, ItemStat> Items { get; set; } = new();
        public Dictionary<string, PrefixStat> Prefixes { get; set; } = new();
    }

    private readonly string _file = Path.Combine(AppPaths.DataDir, "usage.json");
    private readonly object _gate = new();
    private Data _data = new();
    private Timer? _saveTimer;

    public void Load()
    {
        try
        {
            if (File.Exists(_file))
                _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_file)) ?? new Data();
        }
        catch (Exception ex)
        {
            Log.Error("Usage store unreadable, starting fresh", ex);
            _data = new Data();
        }
    }

    public double Boost(Query query, string key)
    {
        lock (_gate)
        {
            double boost = 0;
            if (_data.Items.TryGetValue(key, out var item))
            {
                boost += Math.Min(120, 35 * Math.Log2(1 + item.Count));
                var age = DateTime.UtcNow - item.LastUtc;
                boost += age.TotalDays < 1 ? 40 : age.TotalDays < 7 ? 20 : age.TotalDays < 30 ? 8 : 0;
            }
            if (query.Norm.Length > 0 && _data.Prefixes.TryGetValue(Trim(query.Norm), out var prefix)
                && prefix.Keys.TryGetValue(key, out double weight) && weight > 0.2)
            {
                boost += Math.Min(450, 200 + 120 * weight);
            }
            return boost;
        }
    }

    public void Record(Query query, string key)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (!_data.Items.TryGetValue(key, out var item)) _data.Items[key] = item = new ItemStat();
            item.Count++;
            item.LastUtc = now;

            string norm = Trim(query.Norm);
            for (int len = 1; len <= norm.Length; len++)
            {
                string p = norm[..len];
                if (!_data.Prefixes.TryGetValue(p, out var stat)) _data.Prefixes[p] = stat = new PrefixStat();
                foreach (var k in stat.Keys.Keys.ToList()) stat.Keys[k] *= 0.85; // older choices fade
                stat.Keys[key] = stat.Keys.GetValueOrDefault(key) + 1;
                stat.LastUtc = now;
            }

            if (_data.Prefixes.Count > MaxPrefixes)
            {
                foreach (var old in _data.Prefixes.OrderBy(kv => kv.Value.LastUtc).Take(MaxPrefixes / 5).ToList())
                    _data.Prefixes.Remove(old.Key);
            }
        }
        _saveTimer ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        _saveTimer.Change(1500, Timeout.Infinite);
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_data);
            File.WriteAllText(_file + ".tmp", json);
            File.Move(_file + ".tmp", _file, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Saving usage store failed", ex);
        }
    }

    private static string Trim(string norm) => norm.Length > 24 ? norm[..24] : norm;
}
