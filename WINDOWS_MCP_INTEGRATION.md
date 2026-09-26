# Windows System Tools

Letting the assistant answer questions about this machine — what is eating the
CPU, which services are running, why the disk is full, what starts with Windows.

> Every tool name, action name, and count below was read off the running server
> on 2026-09-25, not from its documentation. Re-check it yourself any time with
> `node SNChat.Tests/probe_windows_mcp.js`.

---

## Turning it on

**Settings → Windows system → "Let the assistant inspect this computer".**
Restart SNChat, and tick the 🔎 toolbar checkbox, which gates every tool and not
just web search.

It needs [Node.js](https://nodejs.org). Nothing else: the server is fetched by
`npx` on first use.

Off by default, because it spawns a process at every launch and what it reports
about the machine is worth opting into rather than discovering.

### Making startup faster

`npx` checks the registry on every launch, which costs a few seconds. Install it
once and point Settings at it directly:

```bash
npm install -g windows-system-mcp
```

Then set **Command** to `windows-system-mcp` and clear **Arguments**.

---

## What it actually provides

**Seven tools, not forty-two.** The server bundles its work into seven tools that
each take an `action` argument, and 56 actions sit behind them. This matters for
more than counting: "list the services" and "stop the print spooler" are *the
same tool* with a different string in one argument, so anything that guards this
server has to read arguments. There is no dangerous tool to leave unregistered.

| Tool | Actions |
|---|---|
| `filesystem` | `list_directory`, `read_file`, `search_files`, `get_file_info`, `find_large_files`, `get_disk_usage` |
| `process_manager` | `list_processes`, `get_process_details`, **`kill_process`**, `find_process`, `get_top_processes`, `get_process_tree` |
| `system_info` | `get_system_overview`, `get_hardware_info`, `get_os_info`, `get_environment_vars`, `get_installed_software`, `get_system_uptime`, `get_user_info`, `get_system_paths` |
| `registry` | `read_key`, `read_value`, `search_keys`, `list_subkeys`, `get_startup_programs`, `get_installed_programs`, `get_system_info_from_registry` |
| `service_manager` | `list_services`, `get_service_details`, **`start_service`**, **`stop_service`**, **`restart_service`**, `get_service_status`, `find_service`, `get_running_services`, `get_startup_services` |
| `network` | `get_network_adapters`, `get_active_connections`, `get_listening_ports`, `get_routing_table`, `ping_host`, `trace_route`, `get_dns_info`, `get_network_statistics`, **`scan_open_ports`**, `get_wifi_profiles` |
| `performance` | `get_cpu_usage`, `get_memory_usage`, `get_disk_usage`, `get_disk_io`, `get_network_io`, `get_system_performance`, `get_top_processes_by_cpu`, `get_top_processes_by_memory`, `get_performance_counters`, `monitor_real_time` |

Bold actions are the ones SNChat guards. Everything else only reports.

Two things the server is often said to have and does not: **the registry is
read-only** — there is no write action of any kind — and there is **no Event
Viewer or Task Scheduler module**. It also cannot write, move, or delete a file;
`filesystem` only reads.

### The overlap that trips models up

Two tools have near-identical action names, and the split is not intuitive:

| Action | Lives on |
|---|---|
| `get_top_processes` | `process_manager` |
| `get_top_processes_by_cpu` | `performance` |
| `get_top_processes_by_memory` | `performance` |
| `get_disk_usage` | **both** `filesystem` and `performance` |

Asked what is eating the CPU, a model reaches for `process_manager` +
`get_top_processes_by_cpu` and gets:

```
❌ Process management operation failed: Unknown action: get_top_processes_by_cpu
```

The server names neither what would have worked nor where that action does live,
so the model guesses again until it runs out of tool iterations and reports that
the machine cannot be inspected.

SNChat appends the missing half:

```
'get_top_processes_by_cpu' is not an action of the 'process_manager' tool.
It belongs to the 'performance' tool - call that one instead, with action
'get_top_processes_by_cpu'. 'process_manager' accepts: list_processes,
get_process_details, kill_process, find_process, get_top_processes,
get_process_tree.
```

The map behind that is built from the tool list the server publishes at startup,
not from a table in the source, so it cannot drift. Only the server's own
`Unknown action` wording is rewritten — a call that failed for a real reason
("No process found with PID 99999") reaches the model untouched, because a model
told to try a different action there would retry instead of reporting.

---

## What SNChat does about the dangerous parts

Five of the 56 actions do something rather than report it. Each is handled where
its risk actually lies.

### Ending a process, and moving a service

Two gates, and both must pass:

1. **A switch in Settings.** Off by default, separately for processes and for
   services. With it off the call is refused and the model is told where the
   switch is — a model told only "no" tries the same thing in a different shape.
2. **A dialog naming the target**, every single time. Not remembered: a folder
   grant answers a standing question the model will hit repeatedly, but a no to
   stopping one service says nothing about the next one.

The dialog names the process or service, which is the entire point. "The
assistant wants to stop a service" is a question nobody can answer; "Service:
MSSQLSERVER" is.

Services are the riskier of the two and are described that way in Settings.
Killing a process loses that program's unsaved work; stopping the wrong service
takes down a database, a VPN, or the machine's ability to log anyone in, and the
damage lands somewhere other than whatever was being asked about.

### Reading a file

The server can reach **every file on every drive** and has no allowed-directories
list of its own — unlike `@modelcontextprotocol/server-filesystem`, there is
nothing to configure. So `read_file` goes through the same folder consent as any
other file access in SNChat: the dialog from `IAccessPrompt`, the session grants
in `SessionAccessGrants`, granted per folder and forgotten when the app closes.
A folder already granted to the filesystem MCP server is already granted here.

Listing a directory, sizing a file, and hunting for large files are deliberately
**not** gated. Those return names and sizes, which is exactly what makes "what is
filling my disk" work, and not what is inside the files.

Drive roots and Windows system folders cannot be granted at all, which is
`SessionAccessGrants`' existing rule and not a new one.

### Scanning another machine's ports

Confirmed with the user whenever the host is not this machine, with no Settings
switch to turn it off permanently. Against `localhost` it is a diagnostic and
runs freely; against anything else it touches a computer that is not yours, and a
standing permission is the wrong shape of answer to that.

An unrecognised host counts as remote, so a name that cannot be classified is
asked about rather than assumed harmless.

### What is not guarded

Hardware, OS version, uptime, installed software, environment variables, the
whole registry, network adapters, connections, listening ports, Wi-Fi profiles,
and every performance counter — all readable without a prompt once the feature is
on. That is a real amount to know about a machine, which is why the feature is
off by default and says so in Settings. The switch to think about is the first
one; the rest are about damage, not disclosure.

---

## How it is wired

No bespoke service class. SNChat already speaks MCP, so this is a server
configuration plus a guard:

```
App startup
  └─ McpService.InitializeAsync()
       ├─ Settings.Tools.McpServers          (hand-written entries)
       └─ Settings.WindowsSystem.Enabled     → one more server config
            └─ for each:
                 1. spawn the process (npx)
                 2. JSON-RPC handshake over stdin/stdout
                 3. ask it for its tool list
                 4. wrap each tool in an McpToolAdapter
                 5. if WindowsSystemGuard.Guards(name):
                      wrap that in a GuardedWindowsSystemTool
                 6. register it in the ToolRegistry
```

| File | What it does |
|---|---|
| `SNChat.Core/Models/AppSettings.cs` | `WindowsSystemSettings` — the switches |
| `SNChat.Core/Services/WindowsSystemGuard.cs` | Which calls need consent, and the words to ask for it. No UI, no I/O |
| `SNChat.Core/Services/WindowsSystemActions.cs` | Turns "Unknown action" into a usable correction |
| `SNChat.Core/Services/IActionPrompt.cs` | Agreeing to one act; `DenyAllActionPrompt` for tests |
| `SNChat.App/Services/GuardedWindowsSystemTool.cs` | The `ITool` decorator that enforces it |
| `SNChat.App/Services/DialogActionPrompt.cs` | The dialog, defaulting to No |
| `SNChat.App/Services/McpService.cs` | Launches the server and applies the wrapper |
| `SNChat.Tests/WindowsSystemGuardTests.cs` | The decisions, in isolation |
| `SNChat.Tests/WindowsSystemActionsTests.cs` | The corrections, in isolation |
| `SNChat.Tests/WindowsSystemServerIntegrationTests.cs` | The decisions against the live server |
| `SNChat.Tests/probe_windows_mcp.js` | Prints what the server really exposes |

The guard keys on **tool names, not on which server they came from**. Configuring
windows-system-mcp by hand as an `McpServers` entry gets exactly the same
protection as switching it on in Settings — and if you do both, the hand-written
entry wins and the second copy is not launched, since two servers with identical
tool names would only collide in the registry.

### Why a decorator

`McpToolAdapter` is generic over every MCP server; this is one server's
particular habits. The model sees no difference — same name, description, and
schema — and the only thing that changes is what happens between the call and the
server.

It stands in front of the server rather than configuring it because there is
nothing to configure. windows-system-mcp takes no allowed-directories list and no
read-only flag. Either the check happens in SNChat or it does not happen.

---

## Configuring by hand

The equivalent `settings.json`, if you prefer JSON to the Settings window:

```json
"WindowsSystem": {
  "Enabled": true,
  "Command": "npx",
  "Arguments": "-y windows-system-mcp",
  "AllowProcessControl": false,
  "AllowServiceControl": false
}
```

It sits at the top level of `%APPDATA%\SNChat\config\settings.json`, beside
`Tools` and `BuildTools` — not inside `Tools`.

---

## Things to ask it

Once it is on:

```
What's using all my CPU right now?
Which services are set to start automatically but aren't running?
What's filling up my C: drive?
How much memory does Chrome have across all its processes?
What programs run when Windows starts?
Why can't I reach the office VPN?
What's listening on port 8080?
```

Questions that need a switch first:

```
Kill whatever is using port 3000        (needs "Allow ending processes")
Restart the Print Spooler service       (needs "Allow starting and stopping services")
```

---

## Troubleshooting

Work down this list; the early items cause most failures.

**Nothing happens when you ask.** Is the 🔎 toolbar checkbox ticked? It gates
every tool, not just web search, and this is the most common cause by a distance.

**Did you restart?** Servers start at launch. Settings changes take effect next
time.

**Does your model support tool calling?** Many small local Ollama models ignore
tool definitions and answer from memory instead. Ask it "what tools do you have?"

**"npx is not recognised".** Node.js is not installed or not on PATH. Restart the
terminal, and SNChat, after installing it.

**The server does not start.** Run it yourself:

```bash
npx -y windows-system-mcp
```

It should print `Windows System MCP Server running on stdio` and then wait — that
is healthy, and Ctrl+C ends it. Errors here are clearer than anything in the log.

**Some answers are empty or partial.** A few actions need administrator rights —
service details and parts of the registry among them. Run SNChat elevated if you
need those, and not otherwise.

**Check the log.** `%APPDATA%\SNChat\logs\snchat-YYYY-MM-DD.log`, searching for
`MCP`. A good start looks like:

```
Connecting to MCP server: Windows system (npx -y windows-system-mcp)
Connected to windows-system-mcp v1.0.0
Discovered 7 tool(s) from Windows system
Successfully registered 7/7 tools from Windows system
```

---

## Keeping this honest

The guard's correctness rests entirely on somebody else's strings — a tool called
`service_manager`, an action called `stop_service`. If the server renames either,
the guard stops recognising the dangerous call and waves it through: no error, no
dialog, and no other test failing.

`WindowsSystemServerIntegrationTests` exists for that. It drives the real server
and fails if any guarded name disappears, and it flags any *new* action whose
name reads like it changes something but that nothing asks about. Both skip
themselves when npx is unavailable, like the other toolchain-dependent tests.

This is not hypothetical. The guide this feature was originally specified from
described 42 tools with names like `process_list`, `file_read`, and
`registry_write`, and a `WindowsSystemService` calling them one by one. The
server has 7 tools, none of those names, and no way to write to the registry at
all; every one of those calls would have failed at runtime. The names in this
document came off the running server, and the tests keep them that way.

---

## References

- [windows-system-mcp on GitHub](https://github.com/guangxiangdebizi/windows-system-mcp) — Apache 2.0, by guangxiangdebizi (Xingyu Chen)
- `MCP_SETUP_GUIDE.md` — configuring other MCP servers by hand
- `MCP_AND_SEARCH_RUNBOOK.md` — diagnosing a setup that stopped working
- [modelcontextprotocol.io](https://modelcontextprotocol.io) — the protocol
