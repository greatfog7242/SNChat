# MCP Setup Guide for SNChat

How to give your AI assistant real access to files, git repositories, and
databases using MCP.

> Every command, package name, and tool name below was verified against a live
> run on 2026-08-30. Where something is easy to get wrong, the guide says so.

---

## What is MCP?

**MCP (Model Context Protocol)** lets the AI use tools outside the chat window:

- Read and write files on your computer
- Run git queries against a repository
- Query SQLite databases
- ...and anything else a community MCP server exposes

An MCP server is a small program SNChat launches in the background. SNChat asks
it "what can you do?", then hands that list of tools to the model. When the model
decides to use one, SNChat forwards the call and returns the result.

---

## Prerequisites

### Node.js — required for the filesystem server

Download the LTS build from [nodejs.org](https://nodejs.org).

```bash
node --version
npm --version
```

Both should print a version. Any recent major version works.

### uv — only if you want the git or SQLite servers

MCP servers come in two flavours and **this trips people up**: some are published
on npm and run with `npx`, others are published on PyPI and run with `uvx`.

| Server | Registry | Runner |
|---|---|---|
| filesystem | npm | `npx` |
| memory | npm | `npx` |
| everything | npm | `npx` |
| **git** | **PyPI** | **`uvx`** |
| **sqlite** | **PyPI** | **`uvx`** |
| **fetch** | **PyPI** | **`uvx`** |
| **time** | **PyPI** | **`uvx`** |

There is no `@modelcontextprotocol/server-git` on npm. Trying to `npx` it fails.

Install uv only if you need those: [astral.sh/uv](https://docs.astral.sh/uv/)

```bash
uvx --version
```

---

## Quick Start: File Access

### Step 1 — Pick a folder

The AI will be able to **read, write, and delete** anything inside the folder you
name. Start with a scratch folder, not your whole user profile:

```bash
mkdir C:\ai-playground
```

### Step 2 — Open your settings file

```
%APPDATA%\SNChat\config\settings.json
```

Press `Win + R`, paste `%APPDATA%\SNChat\config`, press Enter.

**Note the `config` subfolder.** The file is not in the `SNChat` folder directly.

If the file does not exist yet, launch SNChat once and close it — it writes
defaults on first run.

### Step 3 — Add the server

Find the `"Tools"` section and add an `"McpServers"` array:

```json
"Tools": {
  "ImageSource": "Auto",
  "McpServers": [
    {
      "Name": "Playground",
      "Command": "npx",
      "Arguments": "-y @modelcontextprotocol/server-filesystem C:\\ai-playground",
      "Enabled": true
    }
  ]
}
```

Three things to get right:

- **Backslashes are doubled** in JSON: `C:\\ai-playground`, not `C:\ai-playground`
- **Keys are PascalCase** (`Tools`, `McpServers`, `Name`) — that is what SNChat
  writes, so matching it keeps the file consistent. Lowercase is also accepted.
- **The folder path goes at the end of `Arguments`**, after the package name

### Step 4 — Restart SNChat

MCP servers start during application launch. Changes to `settings.json` take
effect on the next start.

### Step 5 — Turn on tools

☑ Tick the **🔎 Web search** checkbox in the toolbar.

**This checkbox controls every tool, not just web search.** With it unchecked,
SNChat sends the model no tools at all and your MCP servers may as well not be
running. This is the single most common reason MCP "does not work".

### Step 6 — Ask for something

```
List the files in C:\ai-playground
Create a file called notes.txt containing "hello from MCP"
Read notes.txt back to me
```

---

## Confirming It Worked

Check the newest log in `%APPDATA%\SNChat\logs\`. A successful start looks like:

```
Initializing 1 MCP server(s)
Connecting to MCP server: Playground (npx -y @modelcontextprotocol/server-filesystem C:\ai-playground)
Connected to secure-filesystem-server v0.2.0
Discovered 14 tool(s) from Playground
Successfully registered 14/14 tools from Playground
MCP initialization complete. 1 server(s) connected, 14 tool(s) registered
```

The server reports itself as `secure-filesystem-server`, which is the internal
name of the `@modelcontextprotocol/server-filesystem` package. That is expected.

---

## The Filesystem Tools

The filesystem server exposes **14 tools**. You never call these directly — the
model picks one based on what you ask — but knowing the list tells you what is
possible.

| Tool | Purpose |
|---|---|
| `read_text_file` | Read a file as text |
| `read_media_file` | Read an image or audio file as base64 |
| `read_multiple_files` | Read several files in one call |
| `read_file` | **Deprecated** — superseded by `read_text_file` |
| `write_file` | Create a file, or overwrite one entirely |
| `edit_file` | Replace specific line sequences |
| `create_directory` | Create a folder, including nested paths |
| `list_directory` | List one folder's contents |
| `list_directory_with_sizes` | Same, with file sizes |
| `directory_tree` | Recursive tree as JSON |
| `move_file` | Move or rename |
| `search_files` | Recursive glob search |
| `get_file_info` | Size, timestamps, and other metadata |
| `list_allowed_directories` | Report which folders are in scope |

Useful when you are unsure of the boundary:

```
Which directories are you allowed to access?
```

---

## Safety

The AI can delete and overwrite files in every folder you configure. `write_file`
overwrites without asking, and `move_file` can rename things out from under you.

**Reasonable:**
- `C:\\ai-playground` — a folder that exists only for this
- `C:\\workspace\\some-project` — a project under version control, so mistakes
  are recoverable with `git checkout`

**Avoid:**
- `C:\\` — the entire drive
- `C:\\Windows` — system files
- Your whole `Documents` folder, if it holds anything you cannot replace

Version control is the real safety net here. Inside a git repository, any damage
is one `git checkout` away from being undone.

### Granting several folders

Add one entry per folder:

```json
"McpServers": [
  {
    "Name": "Docs",
    "Command": "npx",
    "Arguments": "-y @modelcontextprotocol/server-filesystem C:\\workspace\\docs",
    "Enabled": true
  },
  {
    "Name": "Scripts",
    "Command": "npx",
    "Arguments": "-y @modelcontextprotocol/server-filesystem C:\\scripts",
    "Enabled": true
  }
]
```

The filesystem server also accepts multiple paths in one entry, space-separated:

```json
"Arguments": "-y @modelcontextprotocol/server-filesystem C:\\docs C:\\scripts"
```

### Switching a server off

Set `"Enabled": false` to disable it without losing the configuration:

```json
{
  "Name": "Playground",
  "Command": "npx",
  "Arguments": "-y @modelcontextprotocol/server-filesystem C:\\ai-playground",
  "Enabled": false
}
```

---

## Other Servers

### Git — requires `uvx`, not `npx`

```json
{
  "Name": "Project Git",
  "Command": "uvx",
  "Arguments": "mcp-server-git --repository C:\\workspace\\my-repo",
  "Enabled": true
}
```

Then ask: *"What was the last commit?"*, *"Show me uncommitted changes"*,
*"What branch am I on?"*

### SQLite — requires `uvx`

```json
{
  "Name": "App Database",
  "Command": "uvx",
  "Arguments": "mcp-server-sqlite --db-path C:\\data\\app.db",
  "Enabled": true
}
```

Then ask: *"What tables exist?"*, *"How many orders were placed today?"*

### Memory — npm

Gives the model a scratchpad that survives across turns.

```json
{
  "Name": "Memory",
  "Command": "npx",
  "Arguments": "-y @modelcontextprotocol/server-memory",
  "Enabled": true
}
```

### Finding more

Browse [github.com/modelcontextprotocol/servers](https://github.com/modelcontextprotocol/servers).
Check each server's README for whether it is npm (`npx`) or PyPI (`uvx`) — the
distinction decides your `Command` field.

---

## Troubleshooting

Work down this list in order; the early items cause most failures.

### The AI says it cannot access files

**Is the 🔎 Web search checkbox ticked?** It gates every tool. This is the most
common cause by a wide margin.

### Does your model support tool calling?

Tools only work with models trained for them. Many small local Ollama models
silently ignore tool definitions and answer from memory instead. If the log shows
tools registered but the model never calls one, try a model that advertises tool
or function-calling support.

A quick check — ask the model directly:

```
What tools do you have available?
```

### "No MCP servers configured"

- Confirm you edited `%APPDATA%\SNChat\config\settings.json`, including the
  `config` subfolder
- Confirm `McpServers` sits inside the `Tools` object, not at the top level
- Confirm at least one entry has `"Enabled": true`
- Confirm the file is valid JSON — a trailing comma or unescaped backslash
  makes SNChat fall back to defaults silently

Paste the file into [jsonlint.com](https://jsonlint.com) if unsure.

### The server fails to start

Run the exact command from your config in a terminal:

```bash
npx -y @modelcontextprotocol/server-filesystem C:\ai-playground
```

It should start and wait silently — that means it is healthy, and you can press
Ctrl+C. Errors here are the real cause, and the message will be far clearer than
anything in the SNChat log.

`npm error 404` means the package name is wrong. Remember that git and sqlite are
PyPI packages run with `uvx`, not npm packages.

### Tools registered but calls fail

Check that the paths you are asking about are inside a configured folder. Ask:

```
Which directories are you allowed to access?
```

The server refuses anything outside its allowed roots by design.

### Where the logs are

```
%APPDATA%\SNChat\logs\snchat-YYYY-MM-DD.log
```

Search for `MCP` to find the startup sequence.

---

## Full Example

A complete `settings.json` with three servers, one disabled:

```json
{
  "Providers": {
    "FreeTokenApiKey": "",
    "OpenRouterApiKey": "",
    "AnthropicApiKey": "",
    "OpenAIApiKey": "",
    "GoogleApiKey": "",
    "GoogleSearchEngineId": ""
  },
  "Defaults": {
    "Temperature": 0.7,
    "MaxTokens": 2048,
    "TopP": 0.9,
    "DefaultProvider": "Ollama",
    "DefaultModel": ""
  },
  "Tools": {
    "ImageSource": "Auto",
    "WebSource": "Auto",
    "SafeSearch": true,
    "McpServers": [
      {
        "Name": "Workspace Files",
        "Command": "npx",
        "Arguments": "-y @modelcontextprotocol/server-filesystem C:\\workspace",
        "Enabled": true
      },
      {
        "Name": "Project Git",
        "Command": "uvx",
        "Arguments": "mcp-server-git --repository C:\\workspace\\myproject",
        "Enabled": true
      },
      {
        "Name": "App Database",
        "Command": "uvx",
        "Arguments": "mcp-server-sqlite --db-path C:\\data\\app.db",
        "Enabled": false
      }
    ]
  },
  "UI": {
    "Theme": "Light",
    "FontSize": 14,
    "ShowTimestamps": true,
    "EnableMarkdown": true,
    "SidebarWidth": 300
  },
  "Storage": {
    "ConversationsPath": "",
    "AutoSave": true,
    "MaxConversationsToKeep": 1000
  }
}
```

---

## How It Works

```
SNChat starts
  └─ McpService.InitializeAsync()
       └─ for each enabled server:
            1. spawn the process (npx / uvx)
            2. JSON-RPC handshake over stdin/stdout
            3. ask it for its tool list
            4. wrap each tool in an McpToolAdapter
            5. register it in the ToolRegistry
                 └─ now offered to the model alongside
                    web_search and image_search
```

Servers are shut down when SNChat closes. A server that fails to start is logged
and skipped — the others still load, and the app starts normally.

---

## Known Rough Edges

- **The 🔎 checkbox is mislabelled.** It reads "Web search" but gates every tool.
- **No UI for MCP.** Servers are configured by editing JSON by hand.
- **Restart required.** Config changes are not picked up while running.
- **stdio only.** Remote MCP servers over HTTP/SSE are not supported yet.

---

## Further Reading

- `MCP_AND_SEARCH_RUNBOOK.md` — the running setup, its rough edges, and how to
  diagnose a failure; start here when something that worked stops working
- `SNChat.MCP/README.md` — library overview
- `SNChat.MCP/PHASE2_TESTING.md` — deeper testing notes
- [modelcontextprotocol.io](https://modelcontextprotocol.io) — protocol spec
