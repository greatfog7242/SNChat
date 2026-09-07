<#
.SYNOPSIS
    Sets up the things SNChat.App.exe needs but does not bundle.

.DESCRIPTION
    The application itself is a single self-contained executable and needs no
    installer. What it cannot carry with it is the tool environment: Node, which
    both MCP servers launch through; a SearXNG instance for web search; the
    directory the filesystem server is allowed to read; and Ollama, if local
    models are wanted.

    Everything here is idempotent - already-installed components are reported
    and skipped, so re-running after a partial failure is safe.

.PARAMETER OllamaUrl
    Address of an Ollama instance to use instead of installing one locally -
    for a host on the network, http://<host>:11434. Implies -SkipOllama, and
    the address is checked before it is reported as usable.

.PARAMETER SkipSearxng
    Leave SearXNG (and its Docker requirement) out. Web search will not work.

.PARAMETER SearxngPort
    Host port for SearXNG. Must match SEARXNG_URL in the app's settings.

.PARAMETER PlaygroundPath
    Directory the filesystem MCP server is allowed to touch. Must match the
    path in that server's arguments in settings.json.

.EXAMPLE
    .\Install-Dependencies.ps1
    Installs everything.

.EXAMPLE
    .\Install-Dependencies.ps1 -SkipOllama
    OpenRouter-only machine: Node, Docker and SearXNG, no local models.

.EXAMPLE
    .\Install-Dependencies.ps1 -OllamaUrl http://192.168.1.50:11434
    Uses an Ollama instance already running elsewhere on the network. Nothing
    is installed locally for it, and the address is verified.
#>

[CmdletBinding()]
param(
    [switch] $SkipOllama,
    [string] $OllamaUrl,
    [switch] $SkipSearxng,
    [int]    $SearxngPort   = 8888,
    [string] $PlaygroundPath = 'C:\ai-playground',
    [string] $SearxngConfigPath = 'C:\searxng'
)

$ErrorActionPreference = 'Stop'

function Write-Step   ($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Write-Ok     ($m) { Write-Host "  [ok]   $m" -ForegroundColor Green }
function Write-Skip   ($m) { Write-Host "  [skip] $m" -ForegroundColor DarkGray }
function Write-Warn   ($m) { Write-Host "  [warn] $m" -ForegroundColor Yellow }

function Test-Command ($name) {
    return [bool] (Get-Command $name -ErrorAction SilentlyContinue)
}

# winget occasionally reports success while having done nothing, so every
# install is followed by a check for the command it was supposed to provide.
function Install-Package ($id, $probe, $label) {
    if (Test-Command $probe) {
        Write-Skip "$label already present ($(& $probe --version 2>&1 | Select-Object -First 1))"
        return $true
    }

    Write-Host "  installing $label via winget..."
    winget install --id $id --accept-source-agreements --accept-package-agreements --silent | Out-Null

    # A fresh install is not on the PATH of an already-running shell.
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
                [Environment]::GetEnvironmentVariable('Path', 'User')

    if (Test-Command $probe) {
        Write-Ok "$label installed"
        return $true
    }

    Write-Warn "$label did not become available. A new terminal, or a reboot, may be needed."
    return $false
}

# ---------------------------------------------------------------------------

Write-Step 'Checking prerequisites'

if (-not (Test-Command 'winget')) {
    throw 'winget is not available. Install "App Installer" from the Microsoft Store, then re-run.'
}
Write-Ok "winget $(winget --version)"

# ---------------------------------------------------------------------------

Write-Step 'Node.js (required - both MCP servers launch through npx)'

$nodeOk = Install-Package -id 'OpenJS.NodeJS.LTS' -probe 'node' -label 'Node.js'

if ($nodeOk) {
    # Downloading the MCP packages now means the first launch of the app is not
    # a silent two-minute wait while npx fetches them.
    Write-Host '  pre-fetching MCP server packages...'
    foreach ($pkg in @('@modelcontextprotocol/server-filesystem', 'mcp-searxng')) {
        try {
            & npx --yes $pkg --help *> $null
            Write-Ok "cached $pkg"
        } catch {
            Write-Warn "could not pre-fetch $pkg - it will be fetched on first use"
        }
    }
}

# ---------------------------------------------------------------------------

Write-Step "Filesystem sandbox ($PlaygroundPath)"

if (Test-Path $PlaygroundPath) {
    Write-Skip "$PlaygroundPath exists"
} else {
    New-Item -ItemType Directory -Path $PlaygroundPath -Force | Out-Null
    Write-Ok "created $PlaygroundPath"
}
Write-Host '         This path must match the filesystem server arguments in settings.json.' -ForegroundColor DarkGray

# ---------------------------------------------------------------------------

if ($SkipSearxng) {
    Write-Step 'SearXNG (skipped)'
    Write-Warn 'Web search will not work until SEARXNG_URL points at a reachable instance.'
} else {
    Write-Step "SearXNG (web search backend, host port $SearxngPort)"

    $dockerOk = Install-Package -id 'Docker.DockerDesktop' -probe 'docker' -label 'Docker Desktop'

    if (-not $dockerOk) {
        Write-Warn 'Skipping SearXNG: Docker is not available.'
    } else {
        # Docker Desktop needs its engine running, which it is not immediately
        # after install; the daemon is what actually has to answer, not the CLI.
        $engineUp = $false
        foreach ($attempt in 1..30) {
            try { docker info *> $null; if ($LASTEXITCODE -eq 0) { $engineUp = $true; break } } catch { }
            if ($attempt -eq 1) { Write-Host '  waiting for the Docker engine (start Docker Desktop if prompted)...' }
            Start-Sleep -Seconds 4
        }

        if (-not $engineUp) {
            Write-Warn 'The Docker engine did not come up. Start Docker Desktop, then re-run with -SkipOllama to finish this step.'
        } else {
            Write-Ok 'Docker engine responding'

            if (-not (Test-Path $SearxngConfigPath)) {
                New-Item -ItemType Directory -Path $SearxngConfigPath -Force | Out-Null
            }

            $settingsFile = Join-Path $SearxngConfigPath 'settings.yml'

            if (Test-Path $settingsFile) {
                Write-Skip "$settingsFile exists (left untouched)"
            } else {
                # Generated per machine: the secret key signs session cookies and
                # must not be shared between instances.
                $secret = -join ((1..64) | ForEach-Object { '{0:x}' -f (Get-Random -Max 16) })

                @"
use_default_settings: true

server:
  secret_key: $secret
  # Off because this instance is reachable only from localhost; the limiter
  # exists to protect public instances from abuse.
  limiter: false
  image_proxy: true

search:
  # json is off by default; without it the API returns HTML and an MCP client
  # gets nothing it can parse.
  formats:
    - html
    - json
  default_lang: "all"

outgoing:
  request_timeout: 10.0
  max_request_timeout: 15.0
  # Spacing requests makes an instance look less like a scraper, which is what
  # triggers the CAPTCHA suspensions in the first place.
  pool_connections: 10
  pool_maxsize: 10
"@ | Set-Content -Path $settingsFile -Encoding UTF8

                Write-Ok "wrote $settingsFile (json output enabled, fresh secret_key)"
            }

            $existing = docker ps -a --filter 'name=^searxng$' --format '{{.Names}}'
            if ($existing -eq 'searxng') {
                Write-Skip 'container "searxng" already exists'
                docker start searxng *> $null
                Write-Ok 'container started'
            } else {
                Write-Host '  creating the searxng container...'
                docker run -d `
                    --name searxng `
                    --restart unless-stopped `
                    -p "${SearxngPort}:8080" `
                    -v "${SearxngConfigPath}:/etc/searxng" `
                    searxng/searxng | Out-Null
                Write-Ok "container running on port $SearxngPort"
            }

            # The container answering on / is not the same as the JSON API
            # working, and it is the JSON API the MCP server depends on.
            Write-Host '  verifying the JSON API...'
            $verified = $false
            foreach ($attempt in 1..15) {
                Start-Sleep -Seconds 2
                try {
                    $r = Invoke-WebRequest -Uri "http://localhost:$SearxngPort/search?q=test&format=json" `
                                           -UseBasicParsing -TimeoutSec 10
                    if ($r.StatusCode -eq 200) { $verified = $true; break }
                } catch { }
            }

            if ($verified) {
                Write-Ok "JSON API answering on http://localhost:$SearxngPort/"
            } else {
                Write-Warn "SearXNG is up but the JSON API did not answer. Check 'formats' in $settingsFile includes json."
            }
        }
    }
}

# ---------------------------------------------------------------------------

if ($OllamaUrl) {
    Write-Step "Ollama (using $OllamaUrl - nothing installed locally)"

    # Reachability is worth proving now: the alternative is discovering it from
    # an empty model dropdown later, which looks like a fault in the app.
    try {
        $tags = Invoke-RestMethod -Uri "$($OllamaUrl.TrimEnd('/'))/api/tags" -TimeoutSec 10
        $names = @($tags.models | ForEach-Object { $_.name })
        Write-Ok "reachable, serving $($names.Count) model(s)"
        $names | Select-Object -First 8 | ForEach-Object { Write-Host "           $_" -ForegroundColor DarkGray }
    } catch {
        Write-Warn "could not reach $OllamaUrl - $($_.Exception.Message)"
        Write-Host @"
           Check on the serving machine:
             - it was started with OLLAMA_HOST=0.0.0.0 (it listens only on its
               own loopback otherwise, and refuses every remote client)
             - port 11434 is allowed through its firewall
"@ -ForegroundColor DarkGray
    }

    Write-Host "         Set this address in Settings -> Providers -> Ollama Server URL." -ForegroundColor DarkGray
} elseif ($SkipOllama) {
    Write-Step 'Ollama (skipped)'
    Write-Warn 'Local models unavailable. OpenRouter needs no local runtime.'
} else {
    Write-Step 'Ollama (local models)'

    if (Install-Package -id 'Ollama.Ollama' -probe 'ollama' -label 'Ollama') {
        Write-Host '         Models are not installed here - they are large and chosen per machine.' -ForegroundColor DarkGray
        Write-Host '         Pull one with, for example:  ollama pull qwen2.5:7b' -ForegroundColor DarkGray
        Write-Host '         To reuse an existing model directory, set OLLAMA_MODELS to its path.' -ForegroundColor DarkGray
    }
}

# ---------------------------------------------------------------------------

Write-Step 'Summary'

$checks = [ordered]@{
    'Node.js'     = (Test-Command 'node')
    'npx'         = (Test-Command 'npx')
    'Docker'      = (Test-Command 'docker')
    'Ollama'      = (Test-Command 'ollama')
    'Playground'  = (Test-Path $PlaygroundPath)
}

foreach ($name in $checks.Keys) {
    if ($checks[$name]) { Write-Ok $name } else { Write-Warn "$name missing" }
}

try {
    $r = Invoke-WebRequest -Uri "http://localhost:$SearxngPort/search?q=test&format=json" `
                           -UseBasicParsing -TimeoutSec 5
    if ($r.StatusCode -eq 200) { Write-Ok "SearXNG JSON API (port $SearxngPort)" }
} catch {
    Write-Warn "SearXNG JSON API not answering on port $SearxngPort"
}

Write-Host @"

Remaining steps, which this script deliberately does not perform:

  1. Copy SNChat.App.exe anywhere and run it. It carries its own runtime.

  2. Enter API keys in Settings -> Providers. Copying settings.json from
     another machine also copies those keys in plain text; prefer retyping
     them unless you control both machines.

  3. Check the MCP server entries under Tools in settings.json:
       - the filesystem server's path argument matches $PlaygroundPath
       - SEARXNG_URL is http://localhost:$SearxngPort/

  4. Point the app at Ollama in Settings -> Providers -> Ollama Server URL,
     if it is not on this machine. Restart afterwards; the address fixes the
     HTTP client and is not re-read while running.

"@ -ForegroundColor Gray

if (-not $OllamaUrl -and -not $SkipOllama) {
    Write-Host "  5. Pull at least one Ollama model, if local models are wanted.`n" -ForegroundColor Gray
}
