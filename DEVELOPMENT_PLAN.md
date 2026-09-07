# Turn SNChat into a coding agent

> This is the canonical copy. It began life outside the repo under
> `%USERPROFILE%\.claude\plans`, where no session opening this repository would ever
> find it. Edit this one.
>
> For current state — branch, what is uncommitted, machine facts, findings worth not
> rediscovering — see **`HANDOFF.md`**. This file is the plan; that one is the status.

## Status as of 2026-09-06

| Stage | State |
|---|---|
| Foundation — the Project concept | **done** |
| 1 — Close the loop (run and debug) | **done** |
| 2 — Languages | **done** (Maven and Ruby unverifiable here) |
| 3 — Harness: rules and skills | **done** |
| 4 — Looping | **done, verified by a real run** |
| 5 — Subagents | **done, wiring verified; no model has delegated yet** |

**The plan is complete.** Shipped: `list_projects`, `build_project`, `run_tests`,
`run_program`, `read_app_log`, `task_complete`, `git_status`, `git_commit`, `run_subagent`;
projects with per-project autonomy, selectable in the toolbar and managed in Settings;
rules and skills; the autonomous loop; the context meter and auto-compaction that preceded
all of it.

What is left is not more features but use: the untested thing now is model behaviour, not
code. See the risk note at the end of this file, which has aged well.

## Context

SNChat can chat, search, and (as of this week) build and test projects. The goal is for it
to **vibe code**: plan, compose, run, debug and iterate across languages, with the user
able to shape how it works — rules, skills, agents, and looping that is either fully
automatic or step-by-step approved **per project**.

Three things block that today, all confirmed by inspection rather than assumed:

1. **It cannot run what it builds.** `ProcessRunner` is the only process launcher reachable
   from a tool, and at every call site the executable comes from settings or
   `ToolchainLocator` — never from the model. It compiled `well_done.exe` and never saw its
   output, which is why it keeps asking the user to paste results back.
2. **There is no project concept.** `BuildToolSettings.AllowedRoots` is a global permission
   list, `Conversation` has no folder field, and `ConversationMetadata.CustomData` is never
   written to the file so it would not survive a restart. "Per project" requires inventing
   Projects.
3. **Tool calls and results are never persisted.** Both providers build the tool transcript
   in a provider-local list inside one `GenerateStreamAsync` call and discard it;
   `MessageRole` has only `User, Assistant, System`. A turn-to-turn loop would therefore
   forget what its own tools returned, seeing only the prose it wrote about them.

Point 3 is the one that would quietly wreck an autonomous loop, and it is why Stage 4 below
carries a prerequisite rather than being pure UI work.

## Decisions taken

- Order: close the loop first, then languages, then harness, then looping, then subagents.
- Autonomy is **per project**: manual, step-by-step approve, or full automatic.
- "Agents" means **subagents with their own context**, returning a summary.
- Languages: Python, Node/TypeScript, Java, Ruby on Rails, Kotlin/Android.
- Full-auto runs take a **git checkpoint** first and refuse to start in a dirty non-git folder.

## Foundation: the Project concept

Everything per-project hangs off this, so it lands with Stage 1.

**New:** `SNChat.Core/Models/Project.cs` — `Id`, `Name`, `RootPath`, `Autonomy`
(`Manual | StepApprove | FullAuto`), `MaxLoopIterations`, `MaxLoopTokens`, `RequireGitCheckpoint`.

**New:** `SNChat.Core/Services/ProjectService.cs` — markdown + YAML frontmatter under
`%APPDATA%\SNChat\projects`, mirroring `TemplateService` (same `SerializerBuilder` /
`UnderscoredNamingConvention` setup, same "skip and log a bad file" loading).

**Modify:** `SNChat.Core/Models/Conversation.cs` — add `ProjectId`. It must be written and
read in `StorageService.GenerateMarkdown` / `ParseMarkdown` as a real frontmatter key; do
**not** use `CustomData`, which is not serialised and would silently not round-trip.

**Permission model:** `WorkspaceGuard` is constructed per call from
`BuildToolSettings.AllowedRoots`. Change the callers to pass the union of those roots and
the active project's `RootPath`. Creating a project is the deliberate act that grants build
and run permission inside it — state this in the Settings UI, since it changes the boundary.

**UI:** a project selector in the `ChatView` toolbar beside Provider/Model/Mode, and a
Projects tab in Settings for the per-project autonomy and budgets. **There is no folder
picker anywhere in this app today** — adding one is new work (`OpenFolderDialog`).

## Stage 1 — Close the loop (run and debug)

**New:** `SNChat.BuildTools/RunProgramTool.cs` — `path`, `arguments` (array), `stdin`.
Reuse `WorkspaceGuard.Resolve` (already defeats `..` and refuses `C:\workshop` against
`C:\work`) and `ProcessRunner.RunAsync`. Require an existing file with an executable
extension. Gate on a new `BuildToolSettings.AllowRun`.

`arguments` **depends on the uncommitted `ToolArgumentReader` fix** — before it, arrays
reached tools as a string of JSON, which is what broke `edit_file`.

**Modify:** `SNChat.BuildTools/ProcessRunner.cs` — optional stdin, **always closing the
stream even when empty** (a program reading to EOF otherwise hangs for the whole timeout);
separate `StandardError` alongside the existing combined `Output`; a shorter
`RunTimeoutSeconds` (default 60) so a blocked program does not hold the turn for five minutes.

Output is returned raw but capped head-and-tail. Translate the Windows crash codes that a
C++ session actually produces: `0xC0000005` access violation, `0xC0000374` heap corruption,
`0xC00000FD` stack overflow, `0xC000013A` Ctrl+C.

**Modify:** `SNChat.BuildTools/RunTestsTool.cs` — optional `test` filter
(`--filter FullyQualifiedName~` / `-R` / `--tests`), and when filtered, return the failure
**message and stack tail** rather than only the name.

**New:** `SNChat.App/Services/ReadAppLogTool.cs` — tail of the app's own log, `lines` and
`contains`. No model-supplied path, so no traversal surface. Now that `ToolRegistry` logs
tool results, this lets the model see why its own last call was refused.

**Modify:** `SNChat.Core/Tools/ToolRegistry.cs` — warn on name collision. Registration is
last-write-wins and MCP tools register *after* the built-ins, so an MCP server exposing
`build_project` would silently shadow ours.

## Stage 2 — Languages

**Modify:** `SNChat.BuildTools/ProjectLocator.cs` — extend `ProjectKind` and `Identify` with
marker-file detection, and add a toolchain table giving build / run / test commands per kind:

| Kind | Marker | Build | Run | Test |
|---|---|---|---|---|
| Python | `pyproject.toml`, `requirements.txt` | — | `python <script>` | `pytest` |
| Node/TS | `package.json` | `npm run build` | `node <script>` | `npm test` |
| Java | `pom.xml` | `mvn -q compile` | `java -jar` | `mvn test` |
| Kotlin/Android | `build.gradle.kts` + `gradlew` | `gradlew assembleDebug` | see below | `gradlew test` |
| Rails | `Gemfile` + `config/` | `bundle install` | `rails runner` | `rspec` |

Interpreters have the **same off-PATH problem cmake had**, so resolve them through
`ToolchainLocator.Resolve` rather than bare names. `JAVA_HOME` is already set persistently
on this machine to Android Studio's bundled JDK, so Java and Gradle need no discovery dance.

Two honest limits to design around rather than paper over:

- **Android "run" means deploying to a device or emulator** (`adb`), not launching a
  process. Build and test are in scope; run is not, and the tool should say so plainly.
- **A Rails server never exits.** `run_program` is built for run-to-completion. Long-running
  processes need start/stop handles, which is deliberately out of scope — for these, treat
  "still running at timeout, with output captured" as success rather than failure.

## Stage 3 — Harness: rules and skills

**Rules.** Today the system prompt has exactly two contributors, joined with `\n\n`:
`ModeSettings.PromptFor(mode)` then the template's prompt. Introduce a composer that layers
**global rules → project rules → mode → template**, keeping the existing two-part behaviour
intact when no rules exist. Project rules are a `RULES.md` read from the project root.

Fix an existing hole while here: the active system prompt is **not persisted anywhere**, so
it is lost on restart and when loading an old conversation. Store it with the conversation.

Everything added here is automatically counted by the context meter, since
`UpdateContextUsage` already calls `BuildSystemPrompt`.

**Skills.** `TemplateService` already stores markdown + YAML with `{{variables}}` — most of
a skill system. Two gaps: the parser reads exactly seven frontmatter keys and **silently
discards anything else**, and only the *user* can invoke a template (Ctrl+T, into the input
box); the model has no path to one.

- **Modify** `TemplateService.Parse`/`Serialize` to carry `invocable` and `argument_hint`.
- **New** a single `use_skill(name, arguments)` tool plus `list_skills` — one tool pair, not
  one tool per skill, because every registered tool's definition is sent on every request
  and the MCP tools already cost ~16k tokens.

## Stage 4 — Looping

**Prerequisite — persist tool exchanges.** Add `MessageRole.Tool`, fields on `Message` for
the tool name/call id, and storage support in `MessageHeader`/`StorageService`. Without
this, iteration 2 of a loop cannot see what iteration 1's tools returned. This is the
largest hidden cost in the whole plan and should not be discovered late.

**The loop itself.** The clean seam is `ChatViewModel.SendMessageAsync` lines 556–557:
when `GenerateResponseAsync` returns, `IsStreaming` is already false, the CTS is disposed,
the reply is saved, and the meter is refreshed. Wrap that.

Known hazards from the current code, all of which need handling:

- `IsStreaming` is false *between* iterations, so the Send button re-enables and the user
  can start a concurrent turn. Needs a separate `IsAgentRunning` flag guarding both.
- A cancelled turn leaves the assistant message in `Messages` but **not** in
  `CurrentConversation.Messages` — the two collections diverge.
- Cancellation is not observable afterwards: `_cancellationTokenSource` is nulled in the
  `finally`, so the loop cannot ask "was that cancelled?". Needs a flag that survives.
- `AutoCompactIfNeededAsync` itself calls the provider and saves; re-entering before it
  finishes races compaction against the next request.
- `_measuredPromptTokens` / `_measuredThrough` / `_requestedMessageCount` are single-slot
  fields; overlapping turns corrupt the meter.

**Stop signal:** a `task_complete(summary)` tool, so completion is observable rather than
string-matched out of prose. Budgets: iterations, tokens (via `ContextMeter`), wall clock.

**Modes:** `StepApprove` shows the intended next action and waits; `FullAuto` continues
until complete or budget-exhausted, with a working stop button.

**Git checkpoint:** new small service — detect a repo with `git rev-parse`, commit or stash
before a full-auto run, and refuse to start in a dirty non-git folder. Reuse `ProcessRunner`.

**Progress:** `StatusMessage` is one unstructured string with no iteration counter and tool
*results* are never shown at all. A loop needs a real progress surface: iteration N of M,
current tool, and what it returned.

## Stage 5 — Subagents

**New:** agent definitions as markdown + YAML under `%APPDATA%\SNChat\agents` (name,
description, system prompt, allowed tool names, model override) — same pattern as templates.

**New:** a `run_subagent(agent, task)` tool that creates a detached `Conversation`, runs the
Stage 4 loop against it with its own budget and tool subset, and returns a summary to the
parent. Reuse `ConversationCompactor.SummarizeAsync` for the summary — it already does
exactly this shape of work.

### What actually shipped, and where it differs

`SNChat.Core/Models/AgentDefinition.cs`, `Core/Services/AgentDefinitionService.cs`,
`Core/Services/ActiveModel.cs`, `SNChat.LLM/Tools/SubagentTools.cs`, 22 tests.

Two deliberate departures from the plan above:

**It does not run the Stage 4 loop, and does not re-summarise.** A subagent is a single
`GenerateStreamAsync` call with its own tool subset and its own `MaxToolIterations` — the
provider already runs the tool loop inside one call. Nesting the cross-turn loop would have
meant nested checkpoints, nested budgets and a second thing that can call `task_complete`,
for no gain: a subagent doing eight tool calls in one turn is the case that matters.

`ConversationCompactor.SummarizeAsync` is not reused either. The subagent's final answer
*is* its summary — it was asked to report, and it reports. Summarising a summary would add
a second model call, a second chance to lose the detail, and latency, to shorten text that
is already short. Long reports are capped instead, and say that they were.

**Two tools are withheld from every subagent, always** — see `NeverDelegated`, and the
`AgentSignals` finding in `HANDOFF.md`. This was not in the plan and is the sharpest edge
in the feature.

There is **no UI**: agents are markdown files, hand-edited, seeded with two read-only
defaults on first run. Skills and projects both got editors and this did not.

## Verification

Per stage, and each stage must be independently shippable.

**Tests** (`SNChat.Tests`, xUnit, prose names — match `WorkspaceGuardTests.cs`):
- run: path outside roots refused, `..` refused, non-executable refused, stdin closed when
  empty, crash-code translation, output capping keeps head and tail
- languages: marker detection per kind; interpreter resolution falls back like `ToolchainLocator`
- rules: composition order, and that absent rules leave today's two-part prompt unchanged
- loop: budget exhaustion stops; `task_complete` stops; cancel stops; no concurrent turn
  can start while `IsAgentRunning`
- tool persistence: a tool exchange round-trips through `MessageHeader`/`StorageService`

**Integration** (extend `BuildToolsIntegrationTests.cs`, which already drives real cmake):
build the scratch C++ project, **run it**, assert its stdout — the end-to-end proof.

Run the suite with the **registry-only PATH**, as established when fixing the cmake bug; a
developer command prompt hides exactly that class of failure:
```
powershell -NoProfile -Command "$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User'); dotnet test SNChat.Tests/SNChat.Tests.csproj"
```

**Manual, in the app** — the only thing that really proves it:
1. Ask it to build and run `C:\ai-playground\well_done`; expect **"well done"** in the chat
   with no request to paste anything.
2. Break `main.cpp`; confirm it reads the compiler error, fixes, rebuilds and re-runs unaided.
3. Set a project to full-auto, give it a small task, confirm the git checkpoint is taken,
   the loop terminates on `task_complete`, and the stop button works mid-run.

## Handoff — a cold session must be able to resume

This is weeks of work across many sessions, so handoff is a deliverable of every stage,
not a courtesy at the end. It has already bitten once: re-deriving Ollama's context window
from memory produced a confidently wrong answer that shipped.

**`HANDOFF.md` in the repo is the durable artifact**, not this plan file — this file lives
under `%USERPROFILE%\.claude\plans` and a cold session opening the repo will never see it.
Anything here that matters long-term gets copied there.

It already carries the right structure, including a *"Non-obvious findings (worth not
rediscovering)"* section. As of 2026-09-06 it has been brought current with: branch and
commit state, **what is uncommitted**, the stage table below, the build/test/publish
commands, repo conventions, machine-specific environment facts, and this week's findings.

**At the end of every stage, update `HANDOFF.md` with:**

1. **Stage table status** — move the stage to done, note what actually shipped versus what
   was planned.
2. **Uncommitted state** — anything verified but not committed, by file. This is the single
   most dangerous thing to lose.
3. **New non-obvious findings**, in the existing style: what broke, what the evidence was,
   what it cost. Prefer findings that would take an hour to rediscover.
4. **What is verified versus assumed.** Say plainly which paths have been run against real
   toolchains and which are written to spec. Right now: .NET and C++/CMake are verified
   end-to-end; **Gradle/Android has never been run**.
5. **Test count**, so a cold session can tell whether it has broken something.

Keep the *"read this first"* block at the top short enough to actually be read. Stale
detail below it is history and can stay.

## Out of scope

Interactive debugging (breakpoints, variable inspection), arbitrary shell execution,
attaching to running processes, deploying to an Android device, and managing long-running
servers.

## The risk worth stating plainly

The limiting factor will not be these features — it will be model strength. A 27B local
model will loop *confidently* into wrong work, and long autonomous runs are exactly where
that costs the most. The OpenRouter path matters more here than anything in this plan, and
`StepApprove` is the right default until you have watched a given model behave on a real task.
