# MCP & Web Search Runbook

Operational reference for the MCP integration and the SearXNG-backed web
search. Covers how it is wired, what breaks, and how to tell one failure from
another.

**Verified working:** 2026-08-30. Every command and version here was run
against this machine, not reproduced from memory.

---

## Current State

| Component | Value |
|---|---|
| SearXNG | Docker container `searxng`, `localhost:8888` |
| SearXNG config | `C:\searxng\settings.yml` (bind-mounted to `/etc/searxng`) |
| MCP servers | `Playground` (filesystem), `Web Search` (searxng) |
| SNChat settings | `%APPDATA%\SNChat\config\settings.json` |
| Logs | `%APPDATA%\SNChat\logs\snchat-YYYYMMDD.log` |
| Tools registered | 20 = 2 built-in + 14 filesystem + 4 searxng |

### Startup chain

```
Windows login
  ├─ Ollama          startup-folder shortcut
  └─ Docker Desktop  registry Run entry (enabled 2026-08-30)
       └─ searxng     restart policy: unless-stopped
            └─ SNChat spawns `npx mcp-searxng` -> localhost:8888
```

SNChat does **not** start Docker. If Docker is down, SearXNG is down, and
searches fail — see *Silent search failure* below.

---

## Why SearXNG

Every hosted backend the original `WebSearchTool` could use is dead or dying:

| Backend | Status as of 2026 |
|---|---|
| Bing Search API | Retired August 2025 |
| Google Custom Search JSON | Closed to new projects; **shuts down 2027-01-01** |
| Brave Search API | Free tier withdrawn Feb 2026; card required, uncapped overage |
| DuckDuckGo Instant Answer | Never a search engine — encyclopedic entities only |

A Google Cloud project that was never granted access returns:

```
403  "This project does not have the access to Custom Search JSON API."
```

That is Google refusing a new customer, **not** a setting to toggle. Enabling
it is not possible and would expire in 2027 regardless.

Self-hosted SearXNG reaches Google's index with no key, no quota, no per-query
cost, and no query leaving the machine.

---

## Configuration

### settings.json

Keys are **PascalCase** and the file lives in the `config` subfolder. Reading is
case-insensitive since `52d78e4`, but the app writes PascalCase.

```json
"Tools": {
  "McpServers": [
    {
      "Name": "Playground",
      "Command": "npx",
      "Arguments": "-y @modelcontextprotocol/server-filesystem C:\\ai-playground",
      "Enabled": true
    },
    {
      "Name": "Web Search",
      "Command": "npx",
      "Arguments": "-y mcp-searxng",
      "Env": { "SEARXNG_URL": "http://localhost:8888" },
      "Enabled": true
    }
  ]
}
```

`Env` exists because many MCP servers take their endpoint or credentials from
the environment rather than argv.

### Recreating the SearXNG container

`settings.yml` must enable `json`; it is off by default and the API returns
HTML without it.

```bash
# MSYS_NO_PATHCONV stops Git Bash rewriting /etc/searxng into a Windows path,
# which silently produces a container that ignores your config.
MSYS_NO_PATHCONV=1 docker run -d --name searxng --restart unless-stopped \
  -p 8888:8080 -v "C:/searxng:/etc/searxng" searxng/searxng
```

### Registry / runner cheat sheet

MCP servers split across two registries, and the runner is decided by which:

| Server | Package | Runner |
|---|---|---|
| filesystem | `@modelcontextprotocol/server-filesystem` | `npx` |
| memory | `@modelcontextprotocol/server-memory` | `npx` |
| searxng | `mcp-searxng` | `npx` |
| git | `mcp-server-git` | `uvx` |
| sqlite | `mcp-server-sqlite` | `uvx` |

There is no `@modelcontextprotocol/server-git` on npm. `@modelcontextprotocol/server-brave-search`
is deprecated; the current package is `@brave/brave-search-mcp-server`.

---

## Known Rough Edges

### Two search tools, one of them broken

`web_search` (built-in, non-functional) is registered alongside
`searxng_web_search` (working). The model cannot tell them apart and has been
observed calling **both in the same turn**. If it calls only `web_search` the
answer will be empty.

Fix: drop `WebSearchTool` and `ImageSearchTool` registration from
`App.xaml.cs` — SearXNG covers images too.

### The 🔎 checkbox gates every tool

Labelled "Web search", but `WebSearchEnabled` controls the whole
`ToolRegistry` (`ChatViewModel.cs:375`). Unchecked, the model is sent zero
tools and MCP appears dead.

### Engines block automated queries

`brave`, `duckduckgo`, and `startpage` CAPTCHA-block and stay suspended; all
results come from Google via SearXNG. Roughly ten queries in two minutes was
enough to get Google itself suspended during testing. Suspension is in-memory —
`docker restart searxng` clears it. Human-paced use does not trip it, but there
is no redundancy behind Google.

### Model speed dominates, not search

SearXNG answers in under a second. A 27B model takes 45 s and has been seen
taking 10 minutes for one round trip; a search needs at least two. `qwen2.5:7b`
also supports tools and is far faster.

### Auto-start is untested

Docker's Run entry was enabled on 2026-08-30 but has not survived a reboot yet.
Confirm with `docker ps` after the next login before assuming search is live.

---

## Troubleshooting

Work down in order — the early entries cause most failures.

### Model says it has no file/search tools

1. Is the 🔎 checkbox ticked? It gates everything.
2. Does the model support tool calling? Check `capabilities` for `tools`:
   ```bash
   curl -s http://localhost:11434/api/show -d '{"name":"MODEL"}' | grep -o '"capabilities":[^]]*]'
   ```
3. Did the servers connect? Look for `N server(s) connected` in the log.

### Silent search failure

`mcp-searxng` starts and registers its tools **even when SearXNG is
unreachable**. The log will report success; the failure only appears as a tool
error mid-conversation.

```bash
docker ps --filter name=searxng          # container up?
curl -s "http://localhost:8888/search?q=test&format=json" | head -c 200
```

### Searches return zero results

```bash
docker logs searxng 2>&1 | grep -iE "captcha|denied|too many" | tail
```

`Suspended: CAPTCHA` on every engine means the instance is rate-limited.
`docker restart searxng` clears it.

### Ollama hangs with no response

Symptom: `/api/tags` answers instantly but `/api/chat` never returns, even for
a small model. That is a wedged scheduler — usually a dead model runner — not an
app bug.

```bash
curl -s http://localhost:11434/api/ps        # expires_at in the past = idle
```

Fix: stop every `ollama` and `ollama app` process, relaunch
`%LOCALAPPDATA%\Programs\Ollama\ollama app.exe`.

Note that one 27B model fills a 24 GB card. Two concurrent sessions will
starve each other and look exactly like a hang.

### Which tool did the model actually use?

```bash
grep "Executing tool" %APPDATA%\SNChat\logs\snchat-*.log | tail -5
```

`searxng_web_search` = the working path. `web_search` = the dead built-in.

---

## Defects Fixed

Root causes worth remembering, since each was invisible in normal use.

**MCP never connected — three stacked faults** (`dd1b9c9`)

1. `Process.Start` with `UseShellExecute = false` bypasses `PATHEXT`, so `npx`
   (really `npx.cmd`) could not be resolved. `StdioTransport` now resolves bare
   commands against PATH/PATHEXT.
2. Request ids were sent as boxed `int` but arrive deserialized into `object?`
   as `JsonElement`, which never compares equal to a boxed int. Every response
   was read, matched against nothing, and discarded. Pending requests are now
   keyed by the id as text.
3. `Encoding.UTF8` emits a BOM, so the first line written was
   `\uFEFF{"jsonrpc"...}` — invalid JSON. The server discarded `initialize` and
   answered nothing, with no error anywhere. Now `UTF8Encoding(false)`.

**Settings save destroyed MCP config** (`dd1b9c9`)

`SaveSettingsAsync` built a fresh `AppSettings` from only UI-bound fields, so
`McpServers` — hand-written, no editor — reset to empty on every save. Saving
now updates the loaded settings in place.

**Settings read was case-sensitive** (`52d78e4`)

Hand-written `mcpServers` bound to nothing and surfaced as "No MCP servers
configured" with no error. Now reads with `PropertyNameCaseInsensitive`.

**Thinking tokens discarded** (`b276593`)

Ollama returns reasoning in `message.thinking`, which `OllamaMessage` did not
declare. A model can fill only that field for minutes while `content` stays
empty — measured at 127 s to first token — which is indistinguishable from a
hang. Reasoning now streams to the status line and is never saved into the
reply.

---

## Verification

Confirms the whole chain end to end.

```bash
docker ps --filter name=searxng --format '{{.Names}} {{.Status}}'

curl -s "http://localhost:8888/search?q=test&format=json" \
  | python -c "import sys,json;print(len(json.load(sys.stdin)['results']),'results')"

curl -s http://localhost:11434/api/tags >/dev/null && echo "ollama ok"

grep -E "server\(s\) connected|tool\(s\) registered" \
  "$APPDATA/SNChat/logs/snchat-$(date +%Y%m%d).log" | tail -2
```

Expected: container up, non-zero results, `ollama ok`, and
`2 server(s) connected, 18 tool(s) registered`.

---

## Related

- `MCP_SETUP_GUIDE.md` — beginner walkthrough for adding MCP servers
- `SNChat.MCP/README.md` — library overview
- `SNChat.MCP/PHASE1_HANDOFF.md` — protocol implementation
- `SNChat.MCP/PHASE2_TESTING.md` — integration testing notes
