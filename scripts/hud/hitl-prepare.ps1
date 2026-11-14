# Prepares the human-in-the-loop HUD acceptance session (docs/product-completion/HITL-ACCEPTANCE.md). It changes nothing outside:
#   * %USERPROFILE%\Documents\D-ASK\hitl\calc   a throwaway npm project with one deliberate bug (for the code-edit step)
#   * %LOCALAPPDATA%\D-ASK\features.cmd         ONLY if you have none: the product profile plus code edits inside that one folder
#   * results\hitl\<timestamp>\                 where to note the outcome of each step
#   powershell -ExecutionPolicy Bypass -File scripts\hud\hitl-prepare.ps1 [-InstallRoot <folder>]
param([string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\D-ASK'))
$ErrorActionPreference = 'Stop'
$proj = Join-Path $env:USERPROFILE 'Documents\D-ASK\hitl\calc'
New-Item -ItemType Directory -Force $proj | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $proj 'package.json'), '{"name":"calc","version":"1.0.0","scripts":{"test":"node test.js"}}', $utf8)
[IO.File]::WriteAllText((Join-Path $proj 'calc.js'), "function add(a, b) { return a + b; }`nfunction subtract(a, b) { return a + b; }`nmodule.exports = { add, subtract };`n", $utf8)
[IO.File]::WriteAllText((Join-Path $proj 'test.js'), "const c = require('./calc');`nif (c.subtract(5, 2) !== 3) { console.log('not ok 1 subtract: expected 3, got ' + c.subtract(5, 2)); process.exit(1); }`nconsole.log('# pass 1');`n", $utf8)
Write-Output "Throwaway project with a bug: $proj"

$features = Join-Path $env:LOCALAPPDATA 'D-ASK\features.cmd'
$default = Join-Path $InstallRoot 'features.default.cmd'
if (Test-Path $features) {
    Write-Output "You already have $features; add this line to it for the code-edit step:"
    Write-Output "  set DASK_AgentHost__DevEditRoots__0=$(Split-Path $proj)"
} elseif (Test-Path $default) {
    New-Item -ItemType Directory -Force (Split-Path $features) | Out-Null
    $lines = (Get-Content $default) + "set DASK_AgentHost__DevEditRoots__0=$(Split-Path $proj)" + "set DASK_AgentHost__DevProjectRoots__0=$(Split-Path $proj)"
    [IO.File]::WriteAllLines($features, $lines, $utf8)
    Write-Output "Wrote $features (product profile + code edits and tests only inside $(Split-Path $proj)). Delete it afterwards to return to the default profile."
} else { Write-Output "D-ASK is not installed at $InstallRoot; install it first (Install-D-ASK.ps1)." }

$out = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')) ("results\hitl\" + (Get-Date -Format 'yyyyMMddTHHmmss'))
New-Item -ItemType Directory -Force $out | Out-Null
Write-Output "Note each step's outcome in: $out"
Write-Output 'Then start D-ASK from the Start Menu and follow docs\product-completion\HITL-ACCEPTANCE.md.'
