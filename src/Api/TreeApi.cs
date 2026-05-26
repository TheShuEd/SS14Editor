using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace Content.Editor.Editor;

internal sealed partial class ApiRouter
{
    private Task HandleTreeAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var tree = FileTreeService.Build(ctx.PrototypesDir);
        if (Directory.Exists(ctx.EnginePrototypesDir))
        {
            var engineTree = FileTreeService.Build(ctx.EnginePrototypesDir, "", ProtoIndexService.EnginePrefix);
            FileTreeService.MarkReadOnly(engineTree);
            tree.Add(new FileTreeNode
            {
                Name = "⚙ Engine (read-only)",
                Path = "__engine__",
                IsDir = true,
                ReadOnly = true,
                Children = engineTree,
            });
        }
        return HttpJson.WriteAsync(res, tree);
    }

    private async Task HandleMetadataAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var metaPath = Path.Combine(ctx.EditorDir, "metadata.json");
        if (!File.Exists(metaPath))
        {
            await HttpJson.WriteErrorAsync(res, 404, "metadata.json not found. Build the project first.");
            return;
        }
        var bytes = await File.ReadAllBytesAsync(metaPath);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    private Task HandleProtoIndexAsync(HttpListenerRequest req, HttpListenerResponse res)
        => HttpJson.WriteAsync(res, _ctx!.ProtoIndex.Index);

    private async Task HandleGitStatusAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var r = await GitStatusService.QueryAsync(ctx.SolutionRoot, ctx.PrototypesDir);
        await HttpJson.WriteAsync(res, new { available = r.Available, files = r.Files });
    }

    private Task HandleSearchProtosAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var q = req.QueryString["q"] ?? "";
        var type = req.QueryString["type"] ?? "entity";
        var limit = int.TryParse(req.QueryString["limit"], out var l) ? l : 50;
        return HttpJson.WriteAsync(res, _ctx!.ProtoIndex.Search(type, q, limit));
    }

    private async Task HandleRefreshIndexAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        await ctx.ProtoIndex.RebuildAsync();
        await HttpJson.WriteAsync(res, new { count = ctx.ProtoIndex.TotalCount });
    }

    private async Task HandleRenameProtoIdAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var doc = await HttpJson.ReadBodyAsync(req);
        var filePath = doc.GetProperty("path").GetString()!;
        var oldId = doc.GetProperty("oldId").GetString()!;
        var newId = doc.GetProperty("newId").GetString()!;
        var protoType = doc.GetProperty("type").GetString()!;

        var fullPath = PathSecurity.Resolve(ctx.PrototypesDir, filePath);
        if (fullPath == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (!File.Exists(fullPath))
        {
            await HttpJson.WriteErrorAsync(res, 404, "File not found");
            return;
        }

        ctx.ProtoIndex.RefreshFile(fullPath, filePath);
        Logger.Info($"Renamed prototype ID: {protoType}/{oldId} -> {newId} in {filePath}");
        await HttpJson.WriteAsync(res, new { success = true });
    }
}
