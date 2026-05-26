using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Editor.Editor;

/// <summary>
/// Owns the in-memory prototype index. Discovers prototypes across the content
/// prototypes directory and (optionally) a separate read-only engine prototypes
/// directory. Supports incremental refresh and ID search.
/// </summary>
internal sealed class ProtoIndexService
{
    private readonly string _prototypesDir;
    private readonly string _enginePrototypesDir;
    private Dictionary<string, List<ProtoIndexEntry>> _index = new();
    private readonly object _lock = new();

    public const string EnginePrefix = "__engine__/";

    public ProtoIndexService(string prototypesDir, string enginePrototypesDir)
    {
        _prototypesDir = prototypesDir;
        _enginePrototypesDir = enginePrototypesDir;
    }

    public IReadOnlyDictionary<string, List<ProtoIndexEntry>> Index
    {
        get { lock (_lock) return _index; }
    }

    public int TotalCount { get { lock (_lock) return _index.Values.Sum(l => l.Count); } }
    public int TypeCount  { get { lock (_lock) return _index.Count; } }

    public Task RebuildAsync() => Task.Run(RebuildCore);

    public void Rebuild() => RebuildCore();

    private void RebuildCore()
    {
        var idx = Build(_prototypesDir);
        if (Directory.Exists(_enginePrototypesDir))
        {
            var engineIndex = Build(_enginePrototypesDir, readOnly: true, pathPrefix: EnginePrefix);
            foreach (var (type, entries) in engineIndex)
            {
                if (!idx.ContainsKey(type)) idx[type] = new List<ProtoIndexEntry>();
                idx[type].AddRange(entries);
            }
        }
        lock (_lock) _index = idx;
    }

    public Task RefreshFileAsync(string fullPath, string relativePath)
        => Task.Run(() => RefreshFileCore(fullPath, relativePath));

    public void RefreshFile(string fullPath, string relativePath)
        => RefreshFileCore(fullPath, relativePath);

    private void RefreshFileCore(string fullPath, string relativePath)
    {
        var scratch = new Dictionary<string, List<ProtoIndexEntry>>();
        try { YamlPrototypeScanner.Scan(fullPath, relativePath, scratch); }
        catch { /* ignore bad files */ }

        lock (_lock)
        {
            foreach (var list in _index.Values)
                list.RemoveAll(e => e.File == relativePath);

            foreach (var (type, entries) in scratch)
            {
                if (!_index.TryGetValue(type, out var list))
                    _index[type] = list = new List<ProtoIndexEntry>();
                list.AddRange(entries);
            }
        }
    }

    public List<ProtoSearchResult> Search(string type, string query, int limit)
    {
        List<ProtoIndexEntry> entries;
        lock (_lock)
        {
            if (!_index.TryGetValue(type, out var found))
            {
                string? alt = null;
                foreach (var k in _index.Keys)
                {
                    if (string.Equals(k, type, StringComparison.OrdinalIgnoreCase)) { alt = k; break; }
                }
                if (alt == null) return new();
                found = _index[alt];
            }
            entries = new List<ProtoIndexEntry>(found);
        }

        if (string.IsNullOrWhiteSpace(query))
            return entries.Take(limit).Select(e => new ProtoSearchResult { Id = e.Id, Name = e.Name }).ToList();

        var lower = query.ToLowerInvariant();
        var prefix = entries
            .Where(e => e.Id.ToLowerInvariant().StartsWith(lower))
            .Select(e => new ProtoSearchResult { Id = e.Id, Name = e.Name });
        var contains = entries
            .Where(e => !e.Id.ToLowerInvariant().StartsWith(lower) &&
                        (e.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                         (e.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)))
            .Select(e => new ProtoSearchResult { Id = e.Id, Name = e.Name });

        return prefix.Concat(contains).Take(limit).ToList();
    }

    private static Dictionary<string, List<ProtoIndexEntry>> Build(string root, bool readOnly = false, string pathPrefix = "")
    {
        var index = new Dictionary<string, List<ProtoIndexEntry>>();
        if (!Directory.Exists(root)) return index;

        var files = Directory.GetFiles(root, "*.yml", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(root, "*.yaml", SearchOption.AllDirectories));

        foreach (var file in files)
        {
            try
            {
                var rel = pathPrefix + Path.GetRelativePath(root, file).Replace('\\', '/');
                YamlPrototypeScanner.Scan(file, rel, index, readOnly);
            }
            catch { /* skip unreadable */ }
        }
        return index;
    }
}
