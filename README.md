# FileNinja — Flow.Launcher plugin

A Flow.Launcher front-end that talks to a running FileNinja instance over a
named pipe. Lightweight client; all real work happens inside FileNinja.

## What it does today

The plugin accepts three input shapes after the `fn ` action keyword:

### Direct URL queries

Same syntax you use in FileNinja's address bar, forwarded as-is and routed
through `PathClassifier`:

- `fn qry://<everything-query>` → Everything search
  (e.g. `fn qry://d:\ *.mp3`).
- `fn tag://<tag-expression>` → Files matching the tag expression
  (e.g. `fn tag://FAVORITE`, `fn tag://red,green&beta`).

`qry://` goes through `EverythingQueryProvider.SearchTopFilesAsync`, which
prefers the Everything3 handle-based client (bypasses the legacy global
semaphore) and caps results server-side. Broad partial queries that would
otherwise scan the entire index now stay sub-second.

### Natural-language query (AI)

- `fn "<natural language description>"` → Gemini translates the description
  to an EQL query, runs it, and returns the matches.

Triggers only when the closing `"` is typed — partial input like `fn "find`
shows a usage hint. The result list opens with a header row showing the
generated EQL; pressing Enter on the header rewrites the input as
`fn qry://<eql>` so you can refine it by hand.

Requires a Gemini API key configured in FileNinja **Settings → APIs & AI**.
Without it, the plugin reports "Gemini is not configured" and skips the AI
call.

The plugin caches the translation result per natural-language string for the
lifetime of the Flow.Launcher session, so trailing whitespace or repeat
keystrokes don't re-bill the same prompt.

### Modifier keys on a file result

| Key            | Effect                                    |
| -------------- | ----------------------------------------- |
| Enter          | Navigate active pane to the item's folder |
| Shift+Enter    | Navigate the *other* pane                 |
| Ctrl+Enter     | Open the item's folder in a new tab       |
| Alt+Enter      | Reveal in Windows Explorer (selects file) |

On the AI header row, Enter rewrites the input to `fn qry://<eql>` and keeps
Flow.Launcher open so you can edit the query.

## IPC contract

Pipe: `\\.\pipe\FileNinja.Plugin.<username>`
Framing: newline-delimited JSON. One request line → one response line.

```jsonc
// request
{ "id": "...", "method": "ping" | "list" | "aiSearch" | "navigate", "params": { ... } }
// response
{ "id": "...", "ok": true, "result": { ... } }
{ "id": "...", "ok": false, "error": "..." }
```

Methods:

- `ping` → `{ version, pid, ready }`
- `list` `{ path, max? }` → `{ items: [{ name, fullPath, isDirectory, size }] }`
  — `path` is any FileNinja URL (`qry://`, `tag://`, local path, etc.).
- `aiSearch` `{ text, max? }` → `{ query, items: [...] }`
  — `text` is the natural-language description. `query` is the EQL produced by
    Gemini; `items` is the result of running that EQL.
- `navigate` `{ path, pane?: "active"|"left"|"right"|"inactive", newTab? }` → `{}`

A new `list` or `aiSearch` request cancels any in-flight predecessor via a
shared supersede slot — the most recent keystroke wins, stale searches drop
their results.

## Installation (dev workflow)

1. `dotnet build flowlauncher-plugin/FileNinja.FlowLauncher.csproj -c Release`
2. Copy the publish folder to:
   `%APPDATA%\FlowLauncher\Plugins\FileNinja-0.1.0\`
3. Restart Flow.Launcher.
4. Trigger with the action keyword `fn ` (note the trailing space —
   Flow.Launcher only activates the plugin once the keyword + space is typed;
   without it, the query falls through to globally-registered plugins).

Set `FILENINJA_EXE` if FileNinja.exe is not on PATH and the plugin needs to
auto-start the app when no instance is responding.

## Roadmap

- `fn collection://<name>` via the collection provider
- `fn recyclebin://`, `fn thiscomputer://`, `fn network://` virtual roots
- `fnact <command>` for FileNinja key-binding commands
- `fn diff://<a>|<b>` triggering `CompareFilesCommand`
- Multiple AI alternatives shown as separate rows (currently only the top
  alternative runs)
