using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Content.Editor.Editor;

/// <summary>
/// Runs <c>git status --porcelain=v1 -z --untracked-files=all</c> against the
/// project root and returns a map of <c>PrototypesDir</c>-relative paths to
/// a short status code understood by the WebUI:
///   "new"      — untracked or added
///   "modified" — modified, type-change, or renamed-with-content-change
///   "deleted"  — deleted in worktree or index
///   "renamed"  — renamed without content change (source removed, target listed)
///   "conflict" — unmerged
/// Paths outside <c>PrototypesDir</c> are skipped.
/// </summary>
internal static class GitStatusService
{
    /// <summary>
    /// Result for one query. <see cref="Available"/> is false when git is not
    /// installed or the project root is not a git work-tree; the UI should
    /// treat that as "no colouring", not an error.
    /// </summary>
    public sealed class Result
    {
        public bool Available { get; init; }
        public Dictionary<string, string> Files { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<Result> QueryAsync(string solutionRoot, string prototypesDir)
    {
        var result = new Result { Available = false };
        if (string.IsNullOrEmpty(solutionRoot) || !Directory.Exists(solutionRoot))
            return result;

        // Find the git work-tree root (the repo may live above SolutionRoot,
        // e.g. when the user opens a submodule or sub-folder).
        var repoRoot = await TryRunAsync(solutionRoot, "rev-parse --show-toplevel");
        if (repoRoot == null) return result;
        repoRoot = repoRoot.Trim();
        if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
            return result;

        var raw = await TryRunAsync(repoRoot, "status --porcelain=v1 -z --untracked-files=all");
        if (raw == null) return result;

        result = new Result { Available = true };

        // Resolve once so substring math is stable on Windows (\\ vs /).
        var protoFull = Path.GetFullPath(prototypesDir).Replace('\\', '/').TrimEnd('/');
        var repoFull = Path.GetFullPath(repoRoot).Replace('\\', '/').TrimEnd('/');

        // --porcelain=v1 -z: NUL-terminated records. Rename/copy entries are
        // "XY <new>\0<old>\0"; everything else is "XY <path>\0".
        var entries = raw.Split('\0');
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e.Length < 4) continue;

            var x = e[0];
            var y = e[1];
            var path = e.Substring(3);
            string? oldPath = null;

            if (x == 'R' || x == 'C' || y == 'R' || y == 'C')
            {
                // Next NUL-segment holds the original path.
                if (i + 1 < entries.Length)
                {
                    oldPath = entries[i + 1];
                    i++;
                }
            }

            var status = MapStatus(x, y);
            if (status == null) continue;

            TryAdd(result.Files, repoFull, protoFull, path, status);
            if (oldPath != null)
                TryAdd(result.Files, repoFull, protoFull, oldPath, "deleted");
        }

        return result;
    }

    private static void TryAdd(Dictionary<string, string> map, string repoFull, string protoFull, string repoRelative, string status)
    {
        var abs = Path.GetFullPath(Path.Combine(repoFull, repoRelative)).Replace('\\', '/');
        if (!abs.StartsWith(protoFull + "/", StringComparison.OrdinalIgnoreCase) &&
            !abs.Equals(protoFull, StringComparison.OrdinalIgnoreCase))
            return;
        var rel = abs.Substring(protoFull.Length).TrimStart('/');
        if (rel.Length == 0) return;
        // Later entries (e.g. a rename destination) should not be overwritten
        // by an earlier "deleted" for the same path; prefer non-deleted.
        if (map.TryGetValue(rel, out var existing) && existing != "deleted" && status == "deleted") return;
        map[rel] = status;
    }

    private static string? MapStatus(char x, char y)
    {
        if (x == '?' && y == '?') return "new";
        if (x == '!' || y == '!') return null;
        if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D')) return "conflict";
        if (x == 'D' || y == 'D') return "deleted";
        if (x == 'A' || y == 'A') return "new";
        if (x == 'R' || y == 'R') return "renamed";
        if (x == 'M' || y == 'M' || x == 'T' || y == 'T' || x == 'C' || y == 'C') return "modified";
        if (x == ' ' && y == ' ') return null;
        return "modified";
    }

    private static async Task<string?> TryRunAsync(string cwd, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stdout = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);

            return p.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null;
        }
    }
}
