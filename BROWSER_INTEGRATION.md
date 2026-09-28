# Chrome Browser Tools

Letting the assistant browse websites, inspect their rendered DOM, console output,
network traffic, and performance characteristics through Chrome DevTools Protocol.

> Every tool name and count below was read off the running server
> (chrome-devtools-mcp v1.10.1) on 2026-09-25, not from its documentation.
> Re-check it yourself any time with `node SNChat.Tests/probe_chrome_mcp.js`.

---

## Turning it on

**Settings → Browser → "Enable Chrome DevTools integration".**
Restart SNChat, and tick the 🔎 toolbar checkbox, which gates every tool.

It needs [Node.js](https://nodejs.org). Nothing else: the server and Chrome are
fetched by `npx` on first use. The browser runs headless (no visible window) in
an isolated profile by default.

Off by default, because it spawns Chrome at every launch and network access is
worth opting into rather than discovering.

### Making startup faster

`npx` checks the registry on every launch. Install it once and point Settings
at it directly:

```bash
npm install -g chrome-devtools-mcp
```

Then set **Command** to `chrome-devtools-mcp` and adjust **Arguments** as needed.

---

## What it actually provides

**19 tools registered** (out of 30 total). SNChat registers only the observational
and navigation tools. Interaction tools (clicking, typing, filling forms) and
high-risk tools (arbitrary script execution, file uploads) are deliberately
excluded.

### Observational tools (16)

These inspect pages without changing them:

| Tool | Does |
|---|---|
| `list_pages` | Lists all open browser tabs/pages |
| `select_page` | Switches to a different page |
| `take_screenshot` | Captures visible page as PNG |
| `take_snapshot` | Captures full DOM snapshot as HTML |
| `get_css_styles` | Gets computed styles for elements |
| `list_console_messages` | Shows browser console output (errors, logs, warnings) |
| `get_console_message` | Gets details of a specific console message |
| `list_network_requests` | Shows all network requests made by the page |
| `get_network_request` | Gets details of a specific request (headers, body, timing) |
| `performance_start_trace` | Begins Chrome performance trace |
| `performance_stop_trace` | Stops trace and saves it |
| `performance_analyze_insight` | Analyzes performance data |
| `lighthouse_audit` | Runs Lighthouse audit (performance, accessibility, SEO) |
| `take_heapsnapshot` | Captures memory heap snapshot |
| `wait_for` | Waits for element or condition |
| `resize_page` | Changes viewport size |

### Navigation tools (3)

These load pages and are subject to domain allowlist checking:

| Tool | Does | Guard |
|---|---|---|
| `navigate_page` | Navigates to a URL | Domain allowlist |
| `new_page` | Opens a new tab with URL | Domain allowlist |
| `close_page` | Closes a tab | None |

### Excluded tools (11)

Not registered at all:

**Interaction (8):** `click`, `fill`, `fill_form`, `type_text`, `press_key`,
`drag`, `hover`, `handle_dialog`

**High-risk (3):** `evaluate_script` (runs arbitrary JS), `emulate` (sets custom
HTTP headers including Authorization/Cookie), `upload_file` (exfiltration
primitive: sends local files to websites)

---

## Guards and safety

### Domain allowlist

**Settings → Browser → AllowedDomains** — domains the assistant may visit.

- **Empty list** (default): allows all domains, logs each navigation
- **Non-empty list**: enforces the allowlist, refuses others

Subdomains are permitted: `github.com` in the list allows `api.github.com`.

### Isolated profile

**Settings → Browser → UseIsolatedProfile** (default: true)

- **Isolated** (recommended): Chrome runs with no access to your logins, cookies,
  or history. Web content cannot instruct the model to act as you on authenticated
  sites.
- **Non-isolated**: Chrome can access your saved sessions. **Requires a non-empty
  domain allowlist** — Settings validation prevents launching without one, because
  an authenticated browser on any site is too large a blast radius.

Most of what makes Chrome DevTools useful — JS rendering, console errors, network
inspection — needs no session at all.

### File writes

Seven tools can write files (`take_screenshot`, `take_snapshot`,
`get_network_request`, `performance_start_trace`, `performance_stop_trace`,
`take_heapsnapshot`, `lighthouse_audit`).

**Settings → Browser → WorkspaceRoots** — the folders they may write into.

The server enforces this itself. chrome-devtools-mcp refuses any path outside its
configured workspace roots, and with none configured it allows **only the OS temp
directory**. That default is why "save a screenshot to `C:\somewhere`" fails until
a folder is named here:

```
Access denied: path C:\somewhere\shot.png (canonical: C:\somewhere\shot.png)
is not within any of the configured workspace roots.
```

Each entry becomes a `--workspace` argument at launch, so **changing it needs a
restart**. Unlike the filesystem server, SNChat cannot widen the browser's roots
mid-session, which is why there is no consent dialog here — a dialog whose "yes"
could not be honoured would be worse than none. SNChat checks the path first only
so the refusal names the folders that *would* work; the server's own message names
none.

Two consequences worth knowing:

- **Relative paths never work.** The server resolves a bare `shot.png` against its
  own working directory, not yours. Always give a full path.
- **No path at all is often what you want.** `take_screenshot` with no `filePath`
  returns the image inline in the conversation without touching the disk.

---

## What this is good for

**Modern web pages.** The assistant can see what a browser sees: JavaScript-rendered
content, single-page apps, dynamic updates. `curl` and web search see only the
initial HTML; Chrome sees the page after JS runs.

**Debugging.** Console errors, network failures, 404s, CORS issues, slow requests,
and performance bottlenecks all surface in the tools above.

**Testing.** Lighthouse audits for accessibility, SEO, and performance. Screenshots
to verify layout. Network inspection to confirm APIs are called correctly.

**Research.** Browsing documentation sites, checking API endpoints, verifying that
a page loads at all. The isolated profile keeps this safe: no risk of posting,
purchasing, or deleting on your behalf.

**Saving images from a page.** Two different things, both possible:

| Want | Tool | What you get |
|---|---|---|
| A picture of the page, or of one element | `take_screenshot` (with `uid` for an element) | A fresh capture, re-rendered by Chrome. PNG/JPEG/WebP |
| The original image file the site served | `list_network_requests` then `get_network_request` with `responseFilePath` | The exact bytes the server sent, saved to disk |

The second route works because every image the page loaded is a network request,
and its response body is the image itself. Verified 2026-09-28: it wrote a real
PNG (correct `89504e47` magic bytes) from a live page.

Note that `get_network_request` names the file it writes after the parameter, not
after the URL — a `responseFilePath` of `logo.png` is written as
`logo.network-response`. Rename it afterwards if the extension matters.

Both routes need `WorkspaceRoots` configured, or they are refused.

---

## What this deliberately cannot do

**Interact with pages.** No clicking, typing, or form submission. The interaction
tools are not registered, so the model has no way to fill in a form or click a
button. If you need that, use a browser yourself or a dedicated automation tool.

**Act as you on authenticated sites.** The isolated profile (enforced by default)
means the assistant cannot see your logged-in sessions. A website that requires
login shows the login page, which is usually what you want for research — the
public docs and API references are the valuable part, not your private account.

**Run arbitrary JavaScript.** `evaluate_script` is excluded because it bypasses
every other guard: arbitrary JS can navigate, click, read the DOM, post data, and
exfiltrate content. If you need to run code in a page, do it in DevTools yourself.

**Upload files.** `upload_file` is excluded because it is an exfiltration primitive:
"upload this file" and "send this document to a website" are the same operation
described two ways.

---

## Settings reference

```json
{
  "Browser": {
    "Enabled": false,
    "Command": "npx",
    "Arguments": "-y chrome-devtools-mcp@latest --isolated --headless --no-usage-statistics --no-performance-crux",
    "UseIsolatedProfile": true,
    "AllowedDomains": [],
    "WorkspaceRoots": ["C:\\ai-playground"]
  }
}
```

| Setting | Default | Purpose |
|---|---|---|
| `Enabled` | `false` | Whether to launch the server at startup |
| `Command` | `"npx"` | How the server is launched (override for globally installed copy) |
| `Arguments` | (see above) | Server arguments. `--isolated` enforces isolated profile, `--headless` runs without visible window |
| `UseIsolatedProfile` | `true` | Whether to enforce isolated profile. If false, requires non-empty `AllowedDomains` |
| `AllowedDomains` | `[]` | Domains the assistant may navigate to. Empty means allow all (with logging); non-empty enforces the list |
| `WorkspaceRoots` | `[]` | Folders the file-writing tools may save into, passed as `--workspace`. Empty leaves the server on the OS temp directory. Needs a restart to change |

---

## Verified against

- **chrome-devtools-mcp** v1.10.1
- **Node.js** v20.11.1
- **Verification date:** 2026-09-25
- **Probe script:** `SNChat.Tests/probe_chrome_mcp.js`
- **Integration tests:** `SNChat.Tests/BrowserServerIntegrationTests.cs`

The integration tests fail if the server renames a tool we guard, adds a new
high-risk tool we don't recognize, or removes a tool we expect. This prevents the
guard from silently diverging from the server.
