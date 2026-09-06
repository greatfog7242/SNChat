# Coding with SNChat

A guide to letting the assistant write, build, run and fix code on your machine.

This assumes you have never used a tool like this. It does not assume you know what an
"agent" is, and it does not use the word without saying what it means.

---

## Contents

1. [What this actually does](#1-what-this-actually-does)
2. [Before you start](#2-before-you-start)
3. [Your first project](#3-your-first-project)
4. [Your first build](#4-your-first-build)
5. [Letting it work on its own](#5-letting-it-work-on-its-own)
5b. [Granting access to a folder outside the project](#5b-granting-access-to-a-folder-outside-the-project)
6. [Rules: teaching it your habits](#6-rules-teaching-it-your-habits)
7. [Skills: saving instructions you reuse](#7-skills-saving-instructions-you-reuse)
8. [Subagents: sending work away](#8-subagents-sending-work-away)
9. [The context meter](#9-the-context-meter)
10. [When it goes wrong](#10-when-it-goes-wrong)
11. [Where everything is kept](#11-where-everything-is-kept)
12. [Reference: every tool it has](#12-reference-every-tool-it-has)

---

## 1. What this actually does

An ordinary chat assistant can only produce text. You paste the code it writes into your
editor, build it yourself, and paste the errors back. Every round trip goes through you.

SNChat can skip you. It can read your files, write to them, compile them, run the result,
read what the compiler or the program said, and try again — without asking you to relay
anything.

### Tools

The way it does this is called a **tool**. A tool is a small, named capability the assistant
can ask for by name: `read_file`, `build_project`, `run_program`. When it wants one, it does
not write out a request in prose; it makes a structured call, the app runs it, and the result
goes back to the assistant. You see a note in the conversation when this happens.

The assistant cannot invent tools. It has exactly the ones the app gives it, and each one
refuses anything outside what you have permitted. That refusal is the safety model, and it
is worth understanding rather than trusting: see [§3](#3-your-first-project).

### What "vibe coding" means here

Describe what you want in ordinary language and let the assistant do the mechanical parts —
writing the code, building it, running it, reading the error, fixing it, running it again.
You stay in the loop as the person who says what is wanted and whether the result is right.

That last part matters more than the marketing suggests. See [§10](#10-when-it-goes-wrong).

---

## 2. Before you start

### You need a model that can use tools

Not all models can. In the **Provider** and **Model** boxes at the top of the window, pick
one that supports tool calling:

- **Ollama** runs models on your own machine. Free, private, no limits, but a model small
  enough to run locally is noticeably weaker at multi-step work.
- **OpenRouter** calls hosted models over the internet. Costs money per request and sends
  your code to someone else's computer, but the strong models are much better at this.

Start with whichever you have. If the assistant answers "I don't have the ability to run
programs" when you ask it to build something, the model you picked either cannot call tools
or was not given any — check the [Build tools](#3-your-first-project) permission first.

### You should be using git

Not strictly required, but strongly recommended, and **required for unattended running**
(§5). git is what lets you undo everything the assistant did in one command. Without it,
a bad run is yours to clean up by hand.

If you have never used git: install it from [git-scm.com](https://git-scm.com), then in your
project folder run

```
git init
git add -A
git commit -m "Before I let the assistant touch this"
```

That single commit is your way back.

---

## 3. Your first project

A **project** in SNChat is a folder plus the rules for working in it. It does two jobs:

1. **It grants permission to build and run.** Those tools refuse to touch anything that is
   not inside a project folder (or a folder you listed under Settings → Build tools). Adding
   a project *is* the act of saying "you may compile and run things here."

   It does **not** grant permission to read and write files — that is a separate boundary
   with a separate list. See [What it can and cannot reach](#what-it-can-and-cannot-reach).
2. **It sets how much freedom the assistant has** in that folder. A scratch folder is a
   reasonable place to let it run unsupervised. A folder holding work you care about is not.

### Adding one

**Settings → Projects → Add...**, then pick the folder.

Then set, for that project:

| Setting | What it means |
|---|---|
| **How much it may do on its own** | See [§5](#5-letting-it-work-on-its-own). Leave this on *asks before each step* to begin with. |
| **Stop after this many steps** | A hard ceiling on how many times it may continue by itself. Default 25. |
| **Stop after this many minutes** | A wall-clock ceiling. Default 30. One build can take minutes, so step count alone is a poor limit. |
| **Take a git checkpoint before running unattended** | Leave this on. It is your undo. |
| **Rules for this project** | See [§6](#6-rules-teaching-it-your-habits). |

Click **Save project**.

### Restart the app

> **This trips up everyone once.** The build and run tools are only offered to the model if
> at least one project (or allowed folder) exists *when the app starts*. After adding your
> very first project, **close and reopen SNChat**. Until you do, the assistant genuinely has
> no build tools and will tell you so.

You only ever need this for the first one.

### Choosing it

Back in the main window there is a **Project:** box in the toolbar next to Provider and
Model. Pick your project there. Every conversation remembers its own project, so switching
conversations switches folders.

If the box says *(no project)*, the build tools will refuse everything. This is the single
most common reason for "it says it can't find my files."

### What it can and cannot reach

There are **two separate boundaries**, and they are configured in different places. This
catches people out, so it is worth thirty seconds now.

| What it wants to do | Which list decides | Where you change it |
|---|---|---|
| **Build, test, run** a project | SNChat's own list: your projects, plus Settings → Build tools | Settings, inside SNChat |
| **Read or write** a file | The file server's list, fixed when SNChat starts | See below |

So a folder can be buildable but unreadable, or the reverse. If the assistant compiles your
project happily but says it cannot open a file in it, this is why — you changed one list and
not the other.

**Reading and writing** is done by a separate program, the *filesystem MCP server*, which
SNChat starts for you. Its list of folders is the path (or paths) at the end of its command
line, set in `%APPDATA%\SNChat\config\settings.json` under `Tools.McpServers`:

```json
"Arguments": "-y @modelcontextprotocol/server-filesystem C:\\ai-playground D:\\work"
```

Backslashes must be doubled, paths containing spaces need `\"quotes\"` around them, and the
app must be restarted afterwards. Ask the assistant to call `list_allowed_directories` to see
what it actually ended up with.

**For a one-off, you do not need to edit anything.** Just ask for the file:

> please load the file at D:\notes\meeting.md

The assistant will be refused, and SNChat will ask you whether to allow it — naming the
**folder** being opened up, not just the file, since that is what is really being granted.
Say yes and the read continues. See [§5b](#5b-granting-access-to-a-folder-outside-the-project).

Either way, `..\..\Windows` and similar are resolved to a real path before being checked, so
they do not get anybody anywhere.

---

## 4. Your first build

Pick your project in the toolbar, then just ask:

> Build this project and tell me if it compiles.

What happens: it calls `list_projects` to see what is there, works out what kind of project
it is, calls `build_project`, and reads the output back. If the build fails it gets the
compiler's actual errors, with file names and line numbers.

Then try the loop that makes this worth having:

> There's a bug in main.cpp — it crashes on an empty input. Find it, fix it, rebuild, and
> run it to confirm.

It should now go round by itself: read, edit, build, run, read the result. That cycle is the
whole point.

### What it knows how to build

| Kind of project | Detected by | Build | Run | Test |
|---|---|---|---|---|
| .NET (C#) | `.sln`, `.csproj` | `dotnet build` | the built exe | `dotnet test` |
| C++ | `CMakeLists.txt` | `cmake` | the built exe | ctest — needs a build first |
| Python | `pyproject.toml`, `requirements.txt` | syntax check | `python script.py` | pytest, falling back to unittest |
| Node / TypeScript | `package.json` | `npm run build` | `node script.js` | `npm test` |
| Android / Kotlin | `build.gradle.kts` + `gradlew` | `gradlew assembleDebug` | — see below | `gradlew test` |
| Java | `pom.xml` | `mvn compile` | `java -jar` | `mvn test` |
| Ruby on Rails | `Gemfile` | `bundle install` | `rails runner` | `rspec` |

Two honest limits:

- **Android apps cannot be "run."** Running an Android app means installing it on a phone or
  emulator, which is a different thing entirely and is not supported. Building and testing
  work. The tool says so plainly rather than pretending.
- **Servers that never exit** (a Rails server, a web app) do not fit. Running a program has a
  60-second limit, and something still running when the clock runs out is reported with
  whatever output it produced. Use it for programs that finish.

Java and Ruby are **written but never tested** — neither is installed on this machine. If
you have them, they may need adjusting.

### Finding your tools

If you have Visual Studio, tools like `cmake` and `MSBuild` are usually not on your system
PATH. The app looks for them anyway: your settings first, then PATH, then inside every
Visual Studio installation it can find. You normally do not need to configure anything.

If it still cannot find something, put the full path in **Settings → Build tools**.

---

## 5. Letting it work on its own

Normally the assistant answers once and stops. **Autonomy** lets it keep going: fix, build,
check, fix again, until the job is done or a limit stops it.

Set per project, under **Settings → Projects → How much it may do on its own**:

| Mode | Behaviour |
|---|---|
| **Manual** | One reply per message. Ordinary chat. |
| **Asks before each step** | Keeps working, but pops up a box before each further step so you can say no. **This is the default, and the right place to start.** |
| **Runs on its own** | Carries on until it reports finished or a budget runs out. No prompts. |

There is no separate button. You set the mode, then send an ordinary message; if the project
allows it, it keeps going by itself.

### How to phrase the task

An unattended run needs a goal it can check itself against. Compare:

- ❌ "Improve the error handling." — no way to know when that is done.
- ✅ "Run `check.py`, fix whatever it reports, and keep going until every check passes."

The second gives it a test it can run to find out whether it has finished. That is what makes
the loop terminate rather than wander.

### The checkpoint

Before an unattended run starts, the app takes a git checkpoint — a record of exactly where
your code stood before it touched anything.

**It refuses to start unless the folder is a clean git repository.** That means:

- git is installed, and
- the folder is a git repo with at least one commit, and
- there is nothing uncommitted — no edited files, no new untracked files.

If any of those fail, you get a message saying so, and **your message is still answered
once** — it just will not carry on by itself.

This is deliberate. Uncommitted work mixed with the assistant's work cannot be separated
afterwards; if it goes wrong you cannot undo its changes without also losing yours. So:
**commit your own work before starting a run.** That is the whole requirement.

To undo everything a run did:

```
git reset --hard <the commit the checkpoint reported>
```

The assistant can commit, but it cannot push, reset, checkout or clean. It cannot destroy
your checkpoint or send anything anywhere.

### Watching it

The status line under the input shows *"Working on its own — step 3 of 25"*. The **Cancel**
button stops it after the step in progress.

> **A real limitation:** you cannot currently see *what* it is doing — only which step it is
> on. Tool results are not shown. If you want to know what happened, read the log
> (§11) or look at `git log` in the project afterwards.

### Two things you will notice

- **A message you did not write.** In the transcript you will see *"Continue. When the task
  is genuinely finished..."*, marked **Continued automatically**. That is the app prompting
  it to take the next step, not you. It is labelled so you can tell.
- **It stops by calling a tool.** When done, it calls `task_complete` with a summary. That is
  a deliberate signal rather than the app guessing from its prose — otherwise a model writing
  "the task is now complete" as part of a *plan* would end the run halfway.

### Budgets

A run stops at the first of: it reports finished, you cancel, a step fails, the step limit,
or the time limit. Both limits are per project.

---

## 5b. Granting access to a folder outside the project

Sooner or later you will ask for a file that lives somewhere the assistant cannot reach:

> please load the file at D:\notes\meeting.md

It tries, and the file server refuses. Rather than leaving it stuck, SNChat asks you:

```
The assistant is trying to open:
    D:\notes\meeting.md

That is outside the folders it is allowed to read. Allowing it means
giving it access to this folder and everything in it:
    D:\notes

It will be able to read and change files there until you close SNChat.
Nothing is remembered after that.

Allow it?                                          [ Yes ]   [ No ]
```

Say **Yes** and the read goes ahead — the original request completes, so you do not have to
ask twice.

Five things worth knowing:

- **The folder is granted, not the file.** A dialog per file would train you to click Yes
  without reading, so it asks once for the folder. The dialog names the folder for exactly
  that reason — read that line, not the first one.
- **It covers reading *and* writing** in that folder.
- **It lasts until you close SNChat.** Nothing is written down; restart and every grant is
  gone. The permanent version of this is editing the settings file (§3).
- **No is remembered too.** Decline, and it will not ask again for that folder this session,
  and the assistant is told to stop asking. Otherwise a run that retries would put the same
  dialog in front of you a dozen times.
- **Some folders are never offered.** A whole drive (`C:\`) or a Windows system folder is
  refused outright rather than put to you. Handing over an entire machine in answer to a
  question about one file is not a choice a dialog can present fairly. If you genuinely want
  that, edit the settings file, where the decision is deliberate.

**It does not grant building or running.** A build executes code — MSBuild targets,
`build.gradle`, pre-build steps — while reading a file does not, so building somewhere new
stays a deliberate decision you make in Settings. If the assistant can now read a folder but
still refuses to build in it, that is why.

**During an unattended run**, this dialog still appears and the run waits for you. That is
the safe way round — it stops rather than granting itself access — but it does mean a run can
sit waiting if you have walked away.

---

## 6. Rules: teaching it your habits

Rules are standing instructions sent with every message, so you stop repeating yourself.

Two levels:

- **Global rules** — apply everywhere. **Settings → Rules & skills**, then *Save rules*.
- **Project rules** — apply only in one project. **Settings → Projects**, select the project,
  *Rules for this project*, then *Save project rules*. Stored as a plain `RULES.md` in the
  project folder, so it can live in version control alongside the code.

They stack in order — global, then project, then the answering mode, then a template — and
where two disagree, the later one wins. Project rules beat global rules.

Rules that earn their place are the specific ones:

```markdown
- Use 4 spaces, never tabs.
- Never add a dependency without asking me first.
- Tests go in tests/, mirroring the source layout.
- Don't write comments that restate the code.
- Run the tests before telling me something works.
```

Rules cost context (§9), so keep them short. Ten specific lines beat two paragraphs of
general encouragement.

### Modes

The **Mode:** box in the toolbar picks between three standing instructions — Chat, Coding and
Scientific — for how it should answer. Use **Coding** for this kind of work. Reword them
under **Settings → Modes**.

---

## 7. Skills: saving instructions you reuse

A **skill** is a saved prompt with blanks in it that the *assistant* can choose to use. If
you find yourself typing the same instructions repeatedly, make it a skill.

Skills are built on templates. Create a template (**Ctrl+T**, or edit the files directly),
then in **Settings → Rules & skills** tick it in the **Skills** list and click *Save skills*.

Ticking it is what makes it available to the assistant. Untick it and it becomes an ordinary
template that only you can use.

Example — a template called "Review for security":

```markdown
Review {{file}} for security problems. Look specifically at input validation,
injection, and anything handling credentials. Report findings worst first, with
line numbers. Say plainly if you find nothing.
```

Now "run a security review on parser.py" causes it to fetch and follow those instructions
itself.

> Every ticked skill is named in a tool description sent with **every** request, so ticking
> twenty costs you context on every message. Tick the ones worth offering.

---

## 8. Subagents: sending work away

A **subagent** is a second assistant that does one job in a conversation of its own and
reports back a short answer.

### Why this exists

The assistant has a limited amount of memory for one conversation (§9). If it reads nine
files looking for one function, all nine files sit in that memory for the rest of the
conversation, crowding out everything else.

Sending a subagent to do the reading costs you three sentences instead of nine files. The
subagent reads all nine in its own space, which is then thrown away.

A subagent can do nothing the main assistant could not. It is cheaper, not more capable.

### The two you get

| Name | For |
|---|---|
| **explorer** | Reading around a codebase and reporting what it found. Cannot change anything. |
| **checker** | Building, testing and reporting what failed. Does not attempt fixes. |

Both are read-only on purpose.

You do not invoke them. The assistant decides — though you can nudge it:

> Send an explorer to find where authentication is handled, then we'll talk about it.

### Writing your own

There is no editor for these; they are files. Put a `.md` file in

```
%APPDATA%\SNChat\agents\
```

shaped like this:

```markdown
---
name: reviewer
description: Reviews a file and reports problems worst first. Use it when you
  want a second opinion without reading the file into this conversation.
allowed_tools:
  - read_text_file
  - search_files
model: ''
max_tool_iterations: 10
---

You review code and report problems, worst first, with line numbers. Say plainly
when something is fine. You cannot change anything, so do not propose edits as
though you had made them.
```

- **`description`** is what the main assistant reads when deciding whether to delegate.
  Describe the *job*, not the method. A vague description means it never gets used.
- **`allowed_tools`** — names from [§12](#12-reference-every-tool-it-has). Leave it out to
  allow everything. Narrow is better: a subagent that reads does not need to build or commit.
- **`model`** — leave empty to use whatever the conversation is on. Or name a cheap local
  model to send repetitive work there while the main conversation runs on something stronger.
- The text below the `---` is its standing instruction.

Changes take effect immediately; no restart.

Two tools are **never** given to a subagent whatever the file says: `run_subagent` (it would
delegate to itself, forever) and `task_complete` (it would end the main run rather than its
own).

### Turning them off

Delete the files in the agents folder. They will not come back — the defaults are written
once, on first run, and never again.

### An honest caveat

Delegating well is harder than it looks: the assistant has to write a complete, self-contained
task for something that cannot see your conversation and cannot ask a follow-up. Strong models
manage this. Smaller local models often write a vague task and get a vague answer back.

Watch the first few delegations before relying on it.

---

## 9. The context meter

The bar near the top shows how full the conversation's memory is: *"45% · 12.3k/27k"*.

Every model has a fixed limit on how much it can hold at once — the conversation, the
tools' descriptions, your rules, everything. When it fills, the oldest material has to go.

**Compaction** is the app's answer. It asks the model to summarise the older messages and
sends the summary in their place. Nothing is deleted: your conversation still reads in full
on screen, and the full text stays in the saved file. Only the copy sent to the model shrinks.

- Automatic at 80% by default. Change under **Settings → Defaults**.
- Or click **Compact** to do it now.

Things that consume context before you have typed anything: tool descriptions (this is the
big one — every available tool costs), your rules, the mode instruction, ticked skills. If the
meter starts high with an empty conversation, that is why.

---

## 10. When it goes wrong

### It says it cannot build or run anything

In order:

1. Is a project selected in the toolbar? *(no project)* means every build tool refuses.
2. Did you restart after adding your first project? The tools are only offered at startup.
3. Does the model support tool calling? Try a different one.

### It can build my project but says it cannot read the files in it

The two boundaries again (§3). Building and reading are governed by different lists.

Ask for one of the files and say yes to the dialog (§5b) for a one-off, or add the folder to
the file server's command line in `settings.json` to make it permanent.

### It refuses to work on its own

Almost always the checkpoint. The folder must be a git repo with **nothing uncommitted**.
Commit your work and try again. The message says which condition failed.

### It stopped early for no reason

Check the step and minute limits for that project — the defaults are 25 and 30. Raise them,
or split the task.

### It confidently did the wrong thing

This is the failure mode to plan for, and no feature in this app prevents it.

A weaker model will not stop when it is confused. It will keep going, sound certain, and
build a tower of wrong work — and an unattended run is exactly where that costs most, because
nobody is watching.

Practical defences, in order of how much they help:

1. **Use *asks before each step* until you have watched a given model work on a given kind of
   task.** Then decide.
2. **Give it something to check itself against** — a test, a script, an error to make go away.
   Without one it cannot tell finished from stuck.
3. **Keep the runs small.** Ten steps that you read beat sixty you did not.
4. **Commit before every run.** The checkpoint is only useful if you took it.

If a run goes wrong, `git reset --hard <checkpoint>` puts everything back.

### It edited a file I did not want touched

Everything inside the project folder is fair game. If part of it should be off limits, either
say so in the project's rules, or narrow the project to a subfolder.

---

## 11. Where everything is kept

Under `%APPDATA%\SNChat` (paste that into the address bar of Explorer):

| Folder | What |
|---|---|
| `conversations\` | Every conversation, as markdown. Readable and editable. |
| `projects\` | Project definitions. |
| `agents\` | Subagent definitions (§8). |
| `templates\` | Templates and skills (§7). |
| `RULES.md` | Your global rules (§6). |
| `config\settings.json` | Everything from the Settings window. |
| `logs\` | What the app did, including every tool call and its result. |

Project rules live as `RULES.md` in the project folder itself, not here.

Folders you allow through the dialog (§5b) are kept **nowhere**. They exist only in memory
for as long as the app is open, which is why closing it takes them all back.

**The log is the honest record.** When something is not behaving as expected, it is in there —
which tools were registered at startup, every call, and why anything was refused. The
assistant can read it too: ask *"check your log and tell me why that was refused."*

### Settings with no switch in the window

Several things live only in `config\settings.json`, edited by hand with the app closed:

| Setting | Effect |
|---|---|
| `Tools.McpServers` | **Which folders it may read and write** (§3), and the search server. There is no UI for this at all. |
| `BuildTools.AllowRun` | Whether it may run programs. Default on. |
| `BuildTools.AllowCommit` | Whether it may commit. Default on; turning it off means unattended runs stop to ask you. |
| `BuildTools.RunTimeoutSeconds` | Seconds a program may run before it is stopped. Default 60. |

Restart the app after editing this file — it is read once, at startup.

---

## 12. Reference: every tool it has

Which of these are available depends on your settings; several are absent until a project
exists.

**Looking around** — these come from the file server, so they follow the *file* boundary (§3),
not your project list

| Tool | Does |
|---|---|
| `list_projects` | Lists your projects and what kind each is |
| `read_file`, `read_text_file`, `read_multiple_files` | Reads files |
| `read_media_file` | Reads an image or other binary |
| `list_directory`, `list_directory_with_sizes`, `directory_tree` | Lists folder contents |
| `search_files` | Finds files by name |
| `get_file_info` | Size, dates |
| `list_allowed_directories` | **Which folders it may read.** Ask for this first when a file it should be able to see is refused |

**Changing things** — same boundary

| Tool | Does |
|---|---|
| `write_file` | Writes a file whole |
| `edit_file` | Changes part of a file |
| `create_directory`, `move_file` | Folders; renaming and moving |

**Building and running**

| Tool | Does |
|---|---|
| `build_project` | Compiles, and reads back the errors |
| `run_tests` | Runs the test suite, optionally filtered to one test |
| `run_program` | Runs a program, with arguments and input, and reads its output |

**Saving work**

| Tool | Does |
|---|---|
| `git_status` | Lists what has changed |
| `git_commit` | Commits it, with a message it writes |

There is deliberately no push, reset, checkout or clean. Its commits carry a marker, so
`git log` shows which were its and which were yours.

**Working on its own**

| Tool | Does |
|---|---|
| `task_complete` | How it says the job is finished |
| `run_subagent` | Delegates a job (§8) |

**Other**

| Tool | Does |
|---|---|
| `read_app_log` | Reads this app's own log — how it finds out why one of its calls was refused |
| `list_skills`, `use_skill` | Your skills (§7) |
| `image_search` | Finds pictures |
| `searxng_web_search`, `web_url_read` | Web search and page reading, if an MCP search server is configured |

---

## A short version

1. Install git, `git init` and commit your project.
2. **Settings → Projects → Add...**, pick the folder, **restart SNChat**.
3. Pick the project in the toolbar. Set Mode to **Coding**.
4. Ask it to build something. Watch what it does.
5. Ask it to fix a bug and confirm the fix by running it.
   If it needs a file from elsewhere, ask for it and say yes to the dialog (§5b).
6. Once you trust it on that kind of task, set the project to **runs on its own**, commit
   first, and give it a job with a test it can check itself against.
7. Write down the corrections you keep repeating as **rules**.

Start supervised. Move to unattended when a model has earned it on that kind of work — not
before.
