using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Content.Editor.Editor;

internal sealed partial class ApiRouter
{
    private async Task HandleFileAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var relPath = req.QueryString["path"];
        if (string.IsNullOrEmpty(relPath))
        {
            await HttpJson.WriteErrorAsync(res, 400, "Missing 'path' query parameter");
            return;
        }

        bool isEngine = relPath.StartsWith(ProtoIndexService.EnginePrefix);
        var baseDir = isEngine ? ctx.EnginePrototypesDir : ctx.PrototypesDir;
        var actualRel = isEngine ? relPath[ProtoIndexService.EnginePrefix.Length..] : relPath;

        var fullPath = PathSecurity.Resolve(baseDir, actualRel);
        if (fullPath == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }

        if (req.HttpMethod == "GET")
        {
            if (!File.Exists(fullPath))
            {
                await HttpJson.WriteErrorAsync(res, 404, "File not found");
                return;
            }
            var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8);
            await HttpJson.WriteAsync(res, new { content, path = relPath, readOnly = isEngine });
        }
        else if (req.HttpMethod == "POST")
        {
            if (isEngine)
            {
                await HttpJson.WriteErrorAsync(res, 403, "Engine prototypes are read-only");
                return;
            }
            var doc = await HttpJson.ReadBodyAsync(req);
            if (!doc.TryGetProperty("content", out var contentEl))
            {
                await HttpJson.WriteErrorAsync(res, 400, "Missing 'content' in body");
                return;
            }
            var content = contentEl.GetString()!;
            content = content.Replace("\r\n", "\n").Replace("\r", "\n");
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            ctx.FileWatcher.SuppressNext(fullPath);
            // Write bytes directly to guarantee LF (0x0A) on disk — bypasses
            // StreamWriter which may convert \n to \r\n on Windows.
            await File.WriteAllBytesAsync(fullPath, new UTF8Encoding(false).GetBytes(content));
            _ = ctx.ProtoIndex.RefreshFileAsync(fullPath, relPath);
            await HttpJson.WriteAsync(res, new { success = true });
        }
    }

    private async Task HandleRenameFileAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var doc = await HttpJson.ReadBodyAsync(req);
        var oldRel = doc.GetProperty("oldPath").GetString()!;
        var newName = doc.GetProperty("newName").GetString()!;

        var oldFull = PathSecurity.Resolve(ctx.PrototypesDir, oldRel);
        if (oldFull == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        var newFull = Path.Combine(Path.GetDirectoryName(oldFull)!, newName);
        if (PathSecurity.Resolve(ctx.PrototypesDir, Path.GetRelativePath(ctx.PrototypesDir, newFull)) == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (!File.Exists(oldFull))
        {
            await HttpJson.WriteErrorAsync(res, 404, "File not found");
            return;
        }
        File.Move(oldFull, newFull);
        Logger.Info($"File renamed: {oldRel} -> {newName}");
        var newRel = Path.GetRelativePath(ctx.PrototypesDir, newFull).Replace('\\', '/');
        await HttpJson.WriteAsync(res, new { success = true, newPath = newRel });
    }

    private async Task HandleDeleteFileAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var relPath = req.QueryString["path"];
        if (string.IsNullOrEmpty(relPath))
        {
            await HttpJson.WriteErrorAsync(res, 400, "Missing path");
            return;
        }
        var fullPath = PathSecurity.Resolve(ctx.PrototypesDir, relPath);
        if (fullPath == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            Logger.Info($"File deleted: {relPath}");
        }
        await HttpJson.WriteAsync(res, new { success = true });
    }

    private async Task HandleCreateFileAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var doc = await HttpJson.ReadBodyAsync(req);
        var parentDir = doc.TryGetProperty("dir", out var dirEl) ? dirEl.GetString() ?? "" : "";
        var fileName = doc.GetProperty("name").GetString()!;
        var content = doc.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";

        var dirFull = string.IsNullOrEmpty(parentDir)
            ? Path.GetFullPath(ctx.PrototypesDir)
            : PathSecurity.Resolve(ctx.PrototypesDir, parentDir);
        if (dirFull == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        var fileFull = Path.Combine(dirFull, fileName);
        if (PathSecurity.Resolve(ctx.PrototypesDir, Path.GetRelativePath(ctx.PrototypesDir, fileFull)) == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        Directory.CreateDirectory(dirFull);
        content = content.Replace("\r\n", "\n").Replace("\r", "\n");
        ctx.FileWatcher.SuppressNext(fileFull);
        await File.WriteAllTextAsync(fileFull, content, new UTF8Encoding(false));
        var rel = Path.GetRelativePath(ctx.PrototypesDir, fileFull).Replace('\\', '/');
        _ = ctx.ProtoIndex.RefreshFileAsync(fileFull, rel);
        await HttpJson.WriteAsync(res, new { success = true, path = rel });
    }

    private async Task HandleFileStampsAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var doc = await HttpJson.ReadBodyAsync(req);
        var paths = doc.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!).ToList();
        var stamps = new Dictionary<string, long>();
        foreach (var rp in paths)
        {
            var fp = PathSecurity.Resolve(ctx.PrototypesDir, rp);
            stamps[rp] = (fp != null && File.Exists(fp))
                ? File.GetLastWriteTimeUtc(fp).Ticks
                : -1;
        }
        await HttpJson.WriteAsync(res, stamps);
    }
}
