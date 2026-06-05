using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FileNinja.FlowLauncher.Ipc;
using Flow.Launcher.Plugin;

namespace FileNinja.FlowLauncher;

public class Main : IAsyncPlugin
{
    // Matches: opening quote, at least one non-quote character, closing quote,
    // optional trailing whitespace. Fires only once the user has actually closed
    // the quoted block — partial input like `"find` returns no match.
    private static readonly Regex AiQueryRegex = new(@"^""([^""]+)""\s*$", RegexOptions.Compiled);

    private PluginInitContext _ctx = null!;
    private readonly IpcClient _client;
    private string? _fileNinjaExePath;

    // Per-NL-text result cache. Flow.Launcher re-fires QueryAsync on every
    // keystroke; once the user has typed a complete quoted query, every
    // subsequent keystroke (e.g. trailing whitespace) re-triggers the same
    // logical input. Caching the AI translation result keeps us from billing
    // Gemini for identical prompts in a tight burst.
    private string? _cachedAiText;
    private List<Result>? _cachedAiResults;

    // Flow.Launcher activates plugins via Activator.CreateInstance(type), so a
    // public parameterless ctor is required. Tests use the internal overload
    // to point the client at an in-process FakePipeServer.
    public Main() : this(new IpcClient())
    {
        PluginLog.Write("Main ctor (parameterless) invoked by Flow.Launcher");
    }

    internal Main(IpcClient client)
    {
        _client = client;
    }

    public Task InitAsync(PluginInitContext context)
    {
        try
        {
            PluginLog.Write($"InitAsync entry — pipe={_client.PipeName}");
            _ctx = context;
            _fileNinjaExePath = Environment.GetEnvironmentVariable("FILENINJA_EXE");
            PluginLog.Write($"InitAsync done — fileNinjaExe={_fileNinjaExePath ?? "(unset)"}");
        }
        catch (Exception ex)
        {
            PluginLog.WriteException("InitAsync", ex);
        }
        return Task.CompletedTask;
    }

    public Task<List<Result>> QueryAsync(Query query, CancellationToken token)
    {
        PluginLog.Write($"QueryAsync entry — search='{query.Search}' rawQuery='{query.RawQuery}'");
        return QueryAsync(query.Search ?? string.Empty, token);
    }

    /// <summary>
    /// String-form of <see cref="QueryAsync(Query, CancellationToken)"/>. Kept
    /// internal so tests can drive it without going through Flow.Launcher's
    /// internally-constructable <c>Query</c> type.
    /// </summary>
    internal async Task<List<Result>> QueryAsync(string rawInput, CancellationToken token)
    {
        var raw = (rawInput ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(raw))
            return Hint("Type qry://, tag://, or \"natural language query\"");

        var aiMatch = AiQueryRegex.Match(raw);
        if (aiMatch.Success)
        {
            var nlText = aiMatch.Groups[1].Value.Trim();
            if (nlText.Length == 0)
                return Hint("Type a description inside quotes, e.g. \"big pdfs from last month\"");
            return await BuildAiResultsAsync(nlText, token);
        }

        if (IsSupportedScheme(raw))
            return await BuildListResultsAsync(raw, token);

        return Hint("Use qry://<query>, tag://<tag>, or \"<natural language>\"");
    }

    private static bool IsSupportedScheme(string raw)
    {
        return raw.StartsWith("qry://", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("tag://", StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Listing ----------

    private async Task<List<Result>> BuildListResultsAsync(string path, CancellationToken token)
    {
        IpcResponse resp;
        var sw = Stopwatch.StartNew();
        try
        {
            PluginLog.Write($"list ipc start — path='{path}'");
            resp = await _client.SendAsync("list",
                new IpcParams { Path = path, Max = 50 },
                connectTimeoutMs: 1500,
                ct: token);
            PluginLog.Write($"list ipc done — {sw.ElapsedMilliseconds}ms ok={resp.Ok}");
        }
        catch (TimeoutException)
        {
            PluginLog.Write($"list ipc timeout — {sw.ElapsedMilliseconds}ms (FileNinja not running)");
            return OfferLaunchFileNinja("FileNinja is not running.");
        }
        catch (IOException ex)
        {
            PluginLog.Write($"list ipc IOException — {sw.ElapsedMilliseconds}ms — {ex.Message}");
            return OfferLaunchFileNinja("FileNinja is not running.");
        }
        catch (Exception ex)
        {
            PluginLog.WriteException("BuildListResultsAsync", ex);
            return Hint($"FileNinja IPC error: {ex.Message}");
        }

        if (!resp.Ok)
            return Hint(resp.Error ?? "Listing failed.");

        var items = resp.Result?.Items ?? new List<IpcItem>();
        if (items.Count == 0)
            return Hint($"No matches for: {path}");

        var results = new List<Result>(items.Count);
        foreach (var item in items)
            results.Add(BuildFileResult(item));
        return results;
    }

    // ---------- AI translation ----------

    private async Task<List<Result>> BuildAiResultsAsync(string nlText, CancellationToken token)
    {
        if (_cachedAiText == nlText && _cachedAiResults != null)
            return _cachedAiResults;

        IpcResponse resp;
        var sw = Stopwatch.StartNew();
        try
        {
            PluginLog.Write($"aiSearch ipc start — text='{nlText}'");
            resp = await _client.SendAsync("aiSearch",
                new IpcParams { Text = nlText, Max = 50 },
                connectTimeoutMs: 1500,
                ct: token);
            PluginLog.Write($"aiSearch ipc done — {sw.ElapsedMilliseconds}ms ok={resp.Ok}");
        }
        catch (TimeoutException)
        {
            return OfferLaunchFileNinja("FileNinja is not running.");
        }
        catch (IOException)
        {
            return OfferLaunchFileNinja("FileNinja is not running.");
        }
        catch (OperationCanceledException)
        {
            return Hint("AI query cancelled.");
        }
        catch (Exception ex)
        {
            PluginLog.WriteException("BuildAiResultsAsync", ex);
            return Hint($"FileNinja IPC error: {ex.Message}");
        }

        if (!resp.Ok)
            return Hint(resp.Error ?? "AI translation failed.");

        var items = resp.Result?.Items ?? new List<IpcItem>();
        var eql = resp.Result?.Query;

        var results = new List<Result>(items.Count + 1);
        if (!string.IsNullOrWhiteSpace(eql))
        {
            // Header row showing the EQL the AI produced — transparency so the user
            // knows what's actually being searched. Pressing Enter on it copies the
            // EQL to the input field as a qry:// URL (lets the user refine it).
            var headerEql = eql;
            results.Add(new Result
            {
                Title = $"AI → {headerEql}",
                SubTitle = $"Translated from: \"{nlText}\". Enter to refine as qry://",
                IcoPath = "Icon.png",
                Score = 1000,
                Action = ctx =>
                {
                    _ctx.API.ChangeQuery($"fn qry://{headerEql}", true);
                    return false; // keep Flow.Launcher open
                }
            });
        }

        if (items.Count == 0)
        {
            results.Add(new Result
            {
                Title = "No files matched the AI-translated query.",
                SubTitle = eql ?? string.Empty,
                IcoPath = "Icon.png"
            });
        }
        else
        {
            foreach (var item in items)
                results.Add(BuildFileResult(item));
        }

        _cachedAiText = nlText;
        _cachedAiResults = results;
        return results;
    }

    private Result BuildFileResult(IpcItem item)
    {
        var fullPath = item.FullPath ?? "";
        var name = item.Name ?? Path.GetFileName(fullPath);
        return new Result
        {
            Title = name,
            SubTitle = fullPath,
            IcoPath = "Icon.png",
            Action = ctx =>
            {
                var mods = ctx.SpecialKeyState;
                if (mods.AltPressed)
                {
                    TryOpenInExplorer(fullPath);
                    return true;
                }

                string? pane = "active";
                bool newTab = false;
                if (mods.ShiftPressed) pane = "inactive";
                if (mods.CtrlPressed) newTab = true;

                var navTarget = item.IsDirectory ? fullPath : Path.GetDirectoryName(fullPath) ?? fullPath;
                _ = NavigateAsync(navTarget, pane, newTab);
                return true;
            }
        };
    }

    // ---------- Actions ----------

    private async Task NavigateAsync(string path, string? pane, bool newTab)
    {
        try
        {
            var resp = await _client.SendAsync("navigate",
                new IpcParams { Path = path, Pane = pane, NewTab = newTab },
                connectTimeoutMs: 1500,
                ct: CancellationToken.None);

            if (!resp.Ok)
                _ctx.API.ShowMsg("FileNinja", resp.Error ?? "Navigation failed", "Icon.png");
        }
        catch (Exception ex)
        {
            _ctx.API.ShowMsg("FileNinja", $"IPC error: {ex.Message}", "Icon.png");
        }
    }

    private static void TryOpenInExplorer(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath))
                Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
            else if (Directory.Exists(fullPath))
                Process.Start("explorer.exe", $"\"{fullPath}\"");
        }
        catch { /* swallow — Explorer rarely fails meaningfully */ }
    }

    // ---------- Helpers ----------

    private List<Result> OfferLaunchFileNinja(string reason)
    {
        return new List<Result>
        {
            new()
            {
                Title = $"{reason} Press Enter to start FileNinja.",
                SubTitle = _fileNinjaExePath ?? "FileNinja.exe (resolved via PATH)",
                IcoPath = "Icon.png",
                Action = ctx => _client.TryStartFileNinja(_fileNinjaExePath)
            }
        };
    }

    private static List<Result> Hint(string text)
    {
        return new List<Result>
        {
            new() { Title = text, IcoPath = "Icon.png" }
        };
    }
}
