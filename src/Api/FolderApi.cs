using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Content.Editor.Editor;

internal sealed partial class ApiRouter
{
    private async Task HandleCreateFolderAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var doc = await HttpJson.ReadBodyAsync(req);
        var parentDir = doc.TryGetProperty("dir", out var dirEl) ? dirEl.GetString() ?? "" : "";
        var name = doc.GetProperty("name").GetString()!;

        if (!IsValidLeafName(name))
        {
            await HttpJson.WriteErrorAsync(res, 400, "Invalid folder name");
            return;
        }

        var baseDir = string.IsNullOrEmpty(parentDir)
            ? Path.GetFullPath(ctx.PrototypesDir)
            : PathSecurity.Resolve(ctx.PrototypesDir, parentDir);
        if (baseDir == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        var target = Path.Combine(baseDir, name);
        if (PathSecurity.Resolve(ctx.PrototypesDir, Path.GetRelativePath(ctx.PrototypesDir, target)) == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (Directory.Exists(target))
        {
            await HttpJson.WriteErrorAsync(res, 409, "Folder already exists");
            return;
        }
        Directory.CreateDirectory(target);
        var rel = Path.GetRelativePath(ctx.PrototypesDir, target).Replace('\\', '/');
        Logger.Info($"Folder created: {rel}");
        await HttpJson.WriteAsync(res, new { success = true, path = rel });
    }

    private async Task HandleRenameFolderAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var doc = await HttpJson.ReadBodyAsync(req);
        var oldRel = doc.GetProperty("oldPath").GetString()!;
        var newName = doc.GetProperty("newName").GetString()!;

        if (!IsValidLeafName(newName))
        {
            await HttpJson.WriteErrorAsync(res, 400, "Invalid folder name");
            return;
        }

        var oldFull = PathSecurity.Resolve(ctx.PrototypesDir, oldRel);
        if (oldFull == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (!Directory.Exists(oldFull))
        {
            await HttpJson.WriteErrorAsync(res, 404, "Folder not found");
            return;
        }
        var newFull = Path.Combine(Path.GetDirectoryName(oldFull)!, newName);
        if (PathSecurity.Resolve(ctx.PrototypesDir, Path.GetRelativePath(ctx.PrototypesDir, newFull)) == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (Directory.Exists(newFull) || File.Exists(newFull))
        {
            await HttpJson.WriteErrorAsync(res, 409, "Target already exists");
            return;
        }
        Directory.Move(oldFull, newFull);
        _ = ctx.ProtoIndex.RebuildAsync();
        var newRel = Path.GetRelativePath(ctx.PrototypesDir, newFull).Replace('\\', '/');
        Logger.Info($"Folder renamed: {oldRel} -> {newRel}");
        await HttpJson.WriteAsync(res, new { success = true, newPath = newRel });
    }

    private async Task HandleDeleteFolderAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        string? relPath = req.QueryString["path"];
        bool recursive = false;
        if (req.HttpMethod == "POST")
        {
            var doc = await HttpJson.ReadBodyAsync(req);
            if (doc.TryGetProperty("path", out var p)) relPath = p.GetString();
            if (doc.TryGetProperty("recursive", out var r)) recursive = r.GetBoolean();
        }

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
        if (!Directory.Exists(fullPath))
        {
            await HttpJson.WriteAsync(res, new { success = true });
            return;
        }
        var hasContents = Directory.EnumerateFileSystemEntries(fullPath).Any();
        if (hasContents && !recursive)
        {
            await HttpJson.WriteErrorAsync(res, 409, "Folder not empty");
            return;
        }
        Directory.Delete(fullPath, recursive);
        _ = ctx.ProtoIndex.RebuildAsync();
        Logger.Info($"Folder deleted: {relPath} (recursive={recursive})");
        await HttpJson.WriteAsync(res, new { success = true });
    }

    /// <summary>
    /// Validates a single path segment supplied as a folder or file name. Rejects
    /// empty strings, path separators, parent-directory references, and Windows
    /// reserved characters.
    /// </summary>
    private static bool IsValidLeafName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name == "." || name == "..") return false;
        if (name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return true;
    }
}
