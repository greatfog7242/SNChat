# Adding SQLite and Chrome DevTools MCP

**Status: Phase 2 (Chrome read-only) implemented 2026-09-28. SQLite (Phase 1) remains a proposal.**

A phased plan for adding two more MCP servers to SNChat, and the safeguards each
needs. Written 2026-09-25, after the Windows system tools landed in `76eb27f`.

## Implementation Status

✅ **Phase 2 — Chrome, read-only** (2026-09-28) — see `BROWSER_INTEGRATION.md`
- Added `BrowserSettings`, `BrowserGuard`, `GuardedBrowserTool`
- Registered 19 allowed tools (16 observational + 3 navigation)
- Excluded 11 tools (8 interaction + 3 high-risk) per user decision
- Integration tests (`BrowserServerIntegrationTests`) and probe (`probe_chrome_mcp.js`)

⏸️ **Phase 1 — SQLite** (not started)
- Awaiting decision on Phase 0 questions (see below)

⏸️ **Phase 3 — Chrome, interaction** (deliberately not implemented)
- User excluded interaction and high-risk tools
- Chrome profile isolation made configurable (default: isolated)

⏸️ **Phase 4 — Tool budget** (deferred)
- 19 Chrome tools added; total now ~54 registered tools

### Corrections to this document, found while implementing

The plan was written from a tool-list probe only, never from driving the server.
Three things in it were wrong:

1. **"Seven tools ... write wherever they are told, routing around the folder
   consent"** (below) is **false** for v1.10.1. The server has its own workspace
   sandbox: file-writing tools are confined to `--workspace`/`--filesystemRoot`
   directories, defaulting to the OS temp directory. Nothing writes anywhere
   until a root is configured. This is real enforcement, better than the guard
   the plan proposed — so `GuardedBrowserTool` checks paths only to produce an
   error that names the configured roots, and asks for no consent, because
   SNChat cannot widen the server's roots without relaunching it.

2. **The observational tier is 16 tools, not 14.** The plan's own list had 16
   items under a heading that said 14; 16 + 3 + 8 + 3 = 30 checks out.

3. **`get_network_request` takes two path arguments** (`requestFilePath`,
   `responseFilePath`), not one. The first guard missed both, so they would have
   been unchecked.

Also worth recording: `get_network_request` + `responseFilePath` saves the
original bytes of any asset the page loaded, which makes downloading an image
file possible without any interaction tool. That was not anticipated here.

> Every tool name and count below was read off the running servers, not from
> their documentation. The commands used are in each section so they can be
> re-run. Where something was not verified, it says so.

Windows-MCP (the accessibility-tree UI automation server) is deliberately **not**
in this plan — see [Why Windows-MCP is not here](#why-windows-mcp-is-not-here).

---

## Contents

- [What the probes found](#what-the-probes-found)
- [Phase 0 — Decisions and spikes](#phase-0--decisions-and-spikes)
- [Phase 1 — SQLite](#phase-1--sqlite)
- [Phase 2 — Chrome, read-only](#phase-2--chrome-read-only)
- [Phase 3 — Chrome, interaction](#phase-3--chrome-interaction)
- [Phase 4 — Tool budget](#phase-4--tool-budget)
- [Cross-cutting work](#cross-cutting-work)
- [Decisions needed](#decisions-needed-from-you)
- [What was not verified](#what-was-not-verified)

---

## What the probes found

### SQLite: the canonical server is currently broken

```bash
uvx mcp-server-sqlite --db-path C:\some\db.sqlite
```

Crashes on startup against the current MCP SDK:

```
AttributeError: 'Server' object has no attribute 'list_resources'
```

`mcp-server-sqlite` is at 2025.4.25 and has not kept up with the SDK. Pinning the
SDK fixes it:

```bash
uvx --with "mcp<1.3" mcp-server-sqlite --db-path C:\some\db.sqlite
```

That starts cleanly and serves **6 tools**:

| Tool | Does | Risk |
|---|---|---|
| `read_query` | Runs a SELECT | Low |
| `list_tables` | Lists tables | Low |
| `describe_table` | One table's schema | Low |
| `append_insight` | Adds to a memo resource | Low |
| `write_query` | Runs INSERT, UPDATE, or DELETE | **Destructive** |
| `create_table` | Creates a table | **Schema change** |

Note there is no `drop_table` tool. Whether `DROP` can reach the database
*through* `write_query` is an open question — see Phase 0.

### Chrome: 30 tools, and an easier shape to guard

```bash
npx -y chrome-devtools-mcp@latest --headless --isolated
```

Reports `chrome_devtools` v1.10.1 and serves **30 tools**. Unlike
windows-system-mcp, these are separate tools rather than one dispatcher with an
`action` argument — so guarding can happen at registration time by tool name,
with no argument parsing. That makes the guard substantially simpler than
`WindowsSystemGuard`.

**Observational (16)** — `list_pages`, `select_page`, `take_snapshot`,
`take_screenshot`, `get_css_styles`, `list_console_messages`,
`get_console_message`, `list_network_requests`, `get_network_request`,
`performance_start_trace`, `performance_stop_trace`,
`performance_analyze_insight`, `lighthouse_audit`, `take_heapsnapshot`,
`wait_for`, `resize_page`

**Navigation (3)** — `navigate_page`, `new_page`, `close_page`

**Interaction (8)** — `click`, `fill`, `fill_form`, `type_text`, `press_key`,
`drag`, `hover`, `handle_dialog`

**High risk (3)** — `evaluate_script`, `emulate`, `upload_file`

Three findings that shaped the plan:

1. **`upload_file(pageId, uid, filePaths)`** reads a local file and sends it to a
   website. That is an exfiltration primitive, and the most dangerous single tool
   in the set. "Upload a file" and "send this document to a website" are the same
   operation described two ways.

2. **`emulate(..., extraHttpHeaders)`** sets arbitrary HTTP headers, including
   `Authorization` and `Cookie`.

3. **Seven tools take a `filePath` or `outputDirPath`** — `take_screenshot`,
   `take_snapshot`, `get_network_request`, `performance_start_trace`,
   `performance_stop_trace`, `take_heapsnapshot`, `lighthouse_audit`. They write
   wherever they are told, routing around the folder consent that governs every
   other file write in SNChat.

The server also prints its own warning on startup — *"exposes content of the
browser instance to the MCP clients allowing them to inspect, debug, and modify
any data in the browser"* — and phones home by default. `--no-usage-statistics`
and `--no-performance-crux` turn that off.

---

## Phase 0 — Decisions and spikes

No product code. Three things that change the design downstream.

### 0.1 — Can SQL escape the configured database?

The important one. `write_query` advertises INSERT, UPDATE, and DELETE, but it is
unknown whether it *enforces* that or merely rejects SELECT. Test, in order of
how much they matter:

| Statement | Why it matters if allowed |
|---|---|
| `ATTACH DATABASE 'C:\other.db' AS x` | **Reaches a database file outside the configured one.** Defeats the scoping entirely |
| `DROP TABLE` / `ALTER TABLE` | Schema destruction through a tool that claims not to do schema |
| `PRAGMA` | Can alter database behaviour, including `journal_mode` |
| Multiple statements in one call | `INSERT ...; DROP TABLE ...` |
| `INSERT ... SELECT` reading another attached db | Exfiltration between databases |

If `ATTACH` works, statement inspection becomes mandatory rather than a
refinement, and Phase 1 grows.

Roughly twenty minutes against a scratch database.

### 0.2 — Which SQLite server

Pinning `mcp<1.3` on an unmaintained package works, but it is a stale dependency
with a dead upstream and it will rot further. Options:

- **Pin and proceed.** Fastest. Accept that it breaks again eventually.
- **Find a maintained alternative.** Unblocks the long term; costs a survey.
- **Write one.** Six tools is not much, and it would need no guard layer at all
  because the safety could be built in. Largest up-front cost, smallest ongoing.

Worth noting the third option honestly: most of Phase 1 is guard work wrapped
around somebody else's server. A small server written here would not need it.

### 0.3 — The authenticated-profile question

Yours to make; see [Decisions needed](#decisions-needed-from-you).

---

## Phase 1 — SQLite

Smaller surface, and it rehearses the guard pattern on a second server before
Chrome. Roughly mirrors what `WindowsSystemGuard` and `GuardedWindowsSystemTool`
already do.

### Settings

```
SqliteSettings
  Enabled          bool      default false
  DatabasePaths    string[]  the databases it may open
  AllowWrite       bool      default false
```

### Guards

**Switch plus confirmation**, the same two-key model as process and service
control. `AllowWrite` off by default; with it on, the dialog shows **the actual
SQL**. That is the equivalent of naming the service — "the assistant wants to
modify the database" is a question nobody can answer.

**Back up before the first write of a session.** A SQLite database is a single
file, so copying it costs milliseconds and makes everything that follows
undoable. This is the same instinct the repo already has elsewhere:
`AppSettings.cs:68-71` leans on the git checkpoint so that committing stays safe,
and `MCP_SETUP_GUIDE.md` says outright that version control is the real safety
net. A database has no git, so the backup stands in for it.

The backup is what makes the rest tolerable. Without it, every insert needs a
dialog and bulk work becomes unusable; with it, ordinary writes can run freely
under the switch.

**Statement inspection**, scoped by 0.1. At minimum: refuse `ATTACH`, and always
confirm schema changes and any `UPDATE` or `DELETE` with no `WHERE`.

### Reused as-is

`IActionPrompt`, `DialogActionPrompt`, the `ITool` decorator pattern from
`GuardedWindowsSystemTool`, and the tool-name-keyed wrapping in `McpService`.

---

## Phase 2 — Chrome, read-only

Ship the observational two-thirds first. Most of the value — real DOM, JS-rendered
pages, console errors, network inspection — with little of the risk. It also
answers a question worth answering before granting anything more: whether your
models actually drive a 30-tool browser competently.

- **Isolated profile enforced.** Default arguments:
  `--isolated --headless --no-usage-statistics --no-performance-crux`
- **Register only the observational tier.** An unregistered tool needs no guard,
  which is the cheapest safety measure available.
- **Navigation behind a domain allowlist.** `navigate_page` and `new_page` take
  URLs, so this is enforceable. Empty list means allow-and-log; a configured list
  means enforce.
- **`filePath` parameters routed through `SessionAccessGrants`**, exactly as
  `filesystem`/`read_file` was in `76eb27f`. Without this, "save a screenshot"
  writes anywhere on disk and the folder consent model has a hole in it.

### Settings

```
BrowserSettings
  Enabled           bool      default false
  Command           string    "npx"
  Arguments         string    "-y chrome-devtools-mcp@latest --isolated --headless ..."
  AllowedDomains    string[]  empty means allow-and-log
```

---

## Phase 3 — Chrome, interaction

Only once Phase 2 behaves.

| Tier | Tools | Guard |
|---|---|---|
| Interaction | `click`, `fill`, `fill_form`, `type_text`, `press_key`, `drag`, `hover`, `handle_dialog` | Settings switch **and** a non-empty domain allowlist |
| High risk | `evaluate_script`, `emulate`, `upload_file` | Separate switch, off by default, confirm every call |

**`upload_file`** gets its own dialog naming **both** the local file and the
destination origin, for the reason given above.

**`evaluate_script`** is the tool that undermines the others. Arbitrary JS can
navigate, click, read the DOM and post data, so it routes around the domain
allowlist and the interaction switch alike. It should be gated at least as
tightly as `upload_file`, with the script shown in the confirmation.

**`handle_dialog`** is quieter than it looks: accepting a browser dialog can
confirm a deletion or a purchase the model triggered a moment earlier.

### The authenticated profile

Recommendation: keep `--isolated` the default, and make a real-profile mode a
separate explicit switch that **requires** a non-empty domain allowlist.

The reason is specific to this app rather than general caution.
`AppSettings.cs:22-24` already records the threat — web content reaching this
model can try to talk it into building something hostile — and SNChat's build
tools execute code. An authenticated browser changes that from "an attacker can
influence the model" to "an attacker can act as you, signed in." The blast radius
is every account that Chrome profile is logged into.

Most of what makes chrome-devtools-mcp better than web fetch — JS rendering,
console, network — needs no session at all.

---

## Phase 4 — Tool budget

The phase most likely to be skipped and most likely to be regretted.

SNChat sends every tool definition on every request. `ToolSettings.EnabledByDefault`
documents the cost: *"a larger prompt on every turn, which a local model pays for
in noticeably slower replies."*

Current registered tools are roughly 35 — 14 filesystem, 7 Windows system, plus
build, git, search, skills, subagents. Chrome's 30 and SQLite's 6 would **roughly
double that**, on every turn, in every conversation, including ones with nothing
to do with browsers or databases.

Options worth weighing:

- Per-conversation server enablement
- A toolbar selector beside the 🔎 checkbox
- Registering Chrome's observational tier only, expanding on first browser use
- Collapsing Chrome's 30 into a few dispatcher tools with an `action` argument —
  ironically the shape windows-system-mcp uses, which is worse to guard but much
  cheaper per turn

Worth **deciding** before Phase 2 ships, even if built afterwards.

---

## Cross-cutting work

**Probe scripts committed**, as `probe_windows_mcp.js` was. The SQLite SDK
breakage is exactly the kind of thing that recurs and is miserable to rediagnose
from a stack trace in a log.

**Integration tests driving the real servers.** Same rationale as
`WindowsSystemServerIntegrationTests`: the guards rest entirely on somebody
else's strings, and a renamed tool means the guard silently stops matching. For
Chrome specifically, the test should fail when a **new** tool appears unguarded —
that tool list will grow, and a future `download_file` should break a test rather
than ship.

**Documentation** in the house shape: what was verified, when, against what
version, and what was not.

---

## Decisions needed from you

1. **SQLite server** — pin the stale one, survey for a maintained one, or write a
   small one that needs no guard layer? (0.2)
2. **Authenticated Chrome profile** — never, or as an explicit opt-in gated on a
   domain allowlist? This decides how much of Phase 3 exists.
3. **Tool budget** — which approach, and does it block Phase 2?
4. **Phase 3 at all** — Phase 2 alone may cover what you actually want. Worth
   revisiting once it is running rather than deciding now.

---

## What was not verified

Honest gaps in the above:

- **The `--autoConnect` flag** mentioned in the original suggestion. Not tested;
  `--isolated` and `--headless` were.
- **Whether `ATTACH`, `DROP`, or multiple statements pass `write_query`.** This is
  spike 0.1 and is the single biggest open question in Phase 1.
- **Whether chrome-devtools-mcp enforces anything itself.** It was probed for its
  tool list, not driven. It may have its own limits that make some guards
  redundant.
- **Startup cost.** Chrome's launch adds to SNChat's startup, which already pays
  for `npx` on every configured server. Not measured.
- **How well any given model drives 30 browser tools.** The honest answer is that
  this is unknown until Phase 2 runs.

---

## Why Windows-MCP is not here

Considered and deliberately deferred.

`windows-mcp` is on **PyPI, not npm** — `npx windows-mcp` returns 404; it is
`uvx windows-mcp` (0.8.5). But the packaging is not the problem.

It is accessibility-tree UI automation: clicking, typing, and sending keystrokes
to any application on the machine. That resists guarding in a way the other
servers do not:

- **Nothing built so far covers it.** `WindowsSystemGuard` keys on seven specific
  tool names; Windows-MCP's are entirely different, so every click would be
  ungated.
- **It cannot be scoped.** Filesystem servers have allowed directories;
  windows-system-mcp has enumerable actions; Chrome has domains and URLs. "Click
  at these coordinates" has no equivalent boundary — one tool covers every
  application on the machine.
- **The accessibility tree reads whatever is on screen**, including an open
  password manager.
- **No undo.** A click that sends an email has sent it.

The honest obstacle is not that a guard would be hard to build but that there is
no natural line to draw. Every other server here had one — reading versus
killing, listing versus reading contents, a domain allowlist. The only candidate
for UI automation is confirm-everything, which makes it useless.

Worth revisiting if a boundary suggests itself. A tool dump would tell us whether
one exists.
