<#
.SYNOPSIS
  Authenticode signing for a D-ASK package (2026-11-15): inventory, sign D-ASK's own binaries and scripts, verify.

.DESCRIPTION
  -Mode Inventory  Lists every executable, library and PowerShell script in the package with its current Authenticode status, split into
                   D-ASK's own files and third-party files. Changes nothing. Writes signing-inventory.json.
  -Mode Sign       Signs D-ASK's own files (hud-native.exe, dask-ocr.exe, the daemon and Chrome relay assemblies/executables, the package's
                   .ps1 scripts) with signtool, SHA-256 + RFC 3161 timestamp, then verifies every one. Third-party files are listed, never
                   signed as D-ASK's (pass -IncludeThirdPartyUnsigned only if your release policy says to counter-sign them).
  -Mode Verify     Verifies that every D-ASK-owned file carries a valid, timestamped signature that chains to a publicly trusted root.

  CERTIFICATE (one of):
    -Thumbprint <sha1>          a code-signing certificate in Cert:\CurrentUser\My or Cert:\LocalMachine\My (e.g. on a hardware token / HSM)
    -PfxPath <file>             a PFX; its password is read from the DASK_SIGN_PFX_PASSWORD environment variable (never a parameter)
    -TrustedSigningDlib <dll> -TrustedSigningMetadata <json>   Azure Trusted Signing (Azure.CodeSigning.Dlib); the certificate is remote

  The certificate must be a REAL publicly trusted code-signing certificate: not self-issued, with the Code Signing EKU, currently valid,
  chaining to a root in Microsoft's Trusted Root Program (Cert:\LocalMachine\AuthRoot). Self-signed or privately rooted certificates are
  refused: they do not make Windows (SmartScreen, Smart App Control) trust the application and must not be presented as signing.

  Signing changes file contents, so build-package.mjs runs this BEFORE it computes the integrity manifest (node build-package.mjs --sign ...).

.EXAMPLE
  powershell -File scripts\package\Sign-D-ASK.ps1 -PackageRoot dist\desktop\D-ASK-x -Mode Inventory
  powershell -File scripts\package\Sign-D-ASK.ps1 -PackageRoot dist\desktop\D-ASK-x -Mode Sign -Thumbprint 0123...
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [ValidateSet('Inventory', 'Sign', 'Verify')][string]$Mode = 'Inventory',
    [string]$Thumbprint,
    [string]$PfxPath,
    [string]$TrustedSigningDlib,
    [string]$TrustedSigningMetadata,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$SignTool,
    [switch]$IncludeThirdPartyUnsigned,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path.TrimEnd('\')

# ── which files are D-ASK's own ──────────────────────────────────────────────────────────────────────────────────────────────────────
$OwnPatterns = @(
    '^src/hud-native/target/release/(hud-native|dask-ocr)\.exe$',
    '^src/DAsk\.Daemon/bin/Release/net8\.0/DAsk\.[^/]+\.(exe|dll)$',
    '^src/DAsk\.ChromeHost/bin/Release/net8\.0/(dask-chrome-host\.exe|DAsk\.[^/]+\.dll)$',
    '^[^/]+\.ps1$',
    '^scripts/.+\.ps1$'
)
function Get-Rel([string]$full) { $full.Substring($PackageRoot.Length + 1).Replace('\', '/') }
function Test-Own([string]$rel) { foreach ($p in $OwnPatterns) { if ($rel -match $p) { return $true } }; return $false }

function Get-Inventory {
    # Runtime data the installed app creates beside itself (the WebView2 profile) is not part of the package.
    Get-ChildItem -LiteralPath $PackageRoot -Recurse -File |
        Where-Object { $_.Extension -in '.exe', '.dll', '.ps1', '.psm1', '.node', '.sys' -and $_.FullName -notmatch '\\node_modules\\playwright-core\\|\.WebView2\\' } |
        ForEach-Object {
            $rel = Get-Rel $_.FullName
            $sig = Get-AuthenticodeSignature -LiteralPath $_.FullName
            [pscustomobject]@{
                path       = $rel
                own        = (Test-Own $rel)
                status     = [string]$sig.Status
                signer     = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { $null }
                timestamped = [bool]$sig.TimeStamperCertificate
            }
        }
}

# ── the certificate policy: real, publicly trusted code signing only ─────────────────────────────────────────────────────────────────
function Test-PublicCodeSigningCertificate([System.Security.Cryptography.X509Certificates.X509Certificate2]$c) {
    $problems = @()
    if (-not $c.HasPrivateKey) { $problems += 'the certificate has no private key here' }
    if ($c.Subject -eq $c.Issuer) { $problems += 'it is self-signed (subject = issuer); a self-signed certificate is not code signing that Windows trusts' }
    $eku = $c.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] }
    if (-not $eku -or -not ($eku.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' })) { $problems += 'it lacks the Code Signing usage (1.3.6.1.5.5.7.3.3)' }
    $now = Get-Date
    if ($now -lt $c.NotBefore -or $now -gt $c.NotAfter) { $problems += "it is not valid now (valid $($c.NotBefore) to $($c.NotAfter))" }
    $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
    $chain.ChainPolicy.RevocationMode = 'Online'
    $chain.ChainPolicy.RevocationFlag = 'ExcludeRoot'
    $chain.ChainPolicy.ApplicationPolicy.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) | Out-Null
    $built = $chain.Build($c)
    if (-not $built) { $problems += 'its chain does not build to a trusted root: ' + (($chain.ChainStatus | ForEach-Object { $_.StatusInformation.Trim() }) -join '; ') }
    else {
        $root = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate
        # Microsoft's Trusted Root Program roots live in AuthRoot; a root the user or an organisation added (Root) is private trust only.
        if (-not (Get-ChildItem Cert:\LocalMachine\AuthRoot | Where-Object Thumbprint -eq $root.Thumbprint)) {
            $problems += "its root ($($root.Subject)) is not in Microsoft's Trusted Root Program (it was added locally); that is private trust, not public code signing"
        }
    }
    return $problems
}

function Find-SignTool {
    if ($SignTool) { return $SignTool }
    $kits = 'C:\Program Files (x86)\Windows Kits\10\bin'
    $found = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $found) { throw 'signtool.exe was not found. Install the Windows SDK "Signing Tools for Desktop Apps" component.' }
    $found.FullName
}

function Invoke-SignTool([string[]]$a) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'   # signtool reports on stderr; that is a result, not a script error
    try { $out = & $script:Tool @a 2>&1 } finally { $ErrorActionPreference = $prev }
    [pscustomobject]@{ exit = $LASTEXITCODE; text = ($out | Out-String).Trim() }
}

$inventory = @(Get-Inventory)
$own = @($inventory | Where-Object own)
$third = @($inventory | Where-Object { -not $_.own })
$report = [ordered]@{ packageRoot = $PackageRoot; mode = $Mode; at = (Get-Date).ToString('o'); own = $own; thirdParty = $third }

if ($Mode -eq 'Sign') {
    $script:Tool = Find-SignTool
    $certArgs = @()
    if ($TrustedSigningDlib) {
        if (-not (Test-Path $TrustedSigningDlib) -or -not (Test-Path $TrustedSigningMetadata)) { throw 'Azure Trusted Signing needs -TrustedSigningDlib (Azure.CodeSigning.Dlib.dll) and -TrustedSigningMetadata (metadata.json).' }
        $certArgs = @('/dlib', $TrustedSigningDlib, '/dmdf', $TrustedSigningMetadata)
        $report.certificate = 'Azure Trusted Signing (remote; verified after signing)'
    } else {
        $cert = $null
        if ($Thumbprint) {
            $tp = $Thumbprint -replace '\s', ''
            $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My | Where-Object Thumbprint -eq $tp | Select-Object -First 1
            if (-not $cert) { throw "No certificate with thumbprint $tp in CurrentUser\My or LocalMachine\My." }
            $certArgs = @('/sha1', $tp)
        } elseif ($PfxPath) {
            $pw = $env:DASK_SIGN_PFX_PASSWORD
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $pw, 'EphemeralKeySet')
            $certArgs = @('/f', $PfxPath) + $(if ($pw) { @('/p', $pw) } else { @() })
        } else { throw 'No certificate: pass -Thumbprint, -PfxPath (password in DASK_SIGN_PFX_PASSWORD) or -TrustedSigningDlib/-TrustedSigningMetadata.' }
        $problems = @(Test-PublicCodeSigningCertificate $cert)
        if ($problems.Count) {
            $report.refused = $problems
            $report.certificate = $cert.Subject
            if ($ReportPath) { $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8 }
            [Console]::Error.WriteLine("REFUSED: this certificate is not a publicly trusted code-signing certificate. Nothing was signed.`n - " + ($problems -join "`n - "))
            exit 2
        }
        $report.certificate = "$($cert.Subject) (issuer $($cert.Issuer), thumbprint $($cert.Thumbprint), valid to $($cert.NotAfter))"
    }
    $targets = @($own) + $(if ($IncludeThirdPartyUnsigned) { @($third | Where-Object status -eq 'NotSigned') } else { @() })
    $results = @()
    foreach ($t in $targets) {
        $full = Join-Path $PackageRoot $t.path
        $r = Invoke-SignTool (@('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/d', 'D-ASK') + $certArgs + @($full))
        $results += [pscustomobject]@{ path = $t.path; exit = $r.exit; output = $r.text }
        if ($r.exit -ne 0) { Write-Warning "signing failed: $($t.path): $($r.text)" }
    }
    $report.signed = $results
    $Mode = 'Verify'
    $inventory = @(Get-Inventory); $own = @($inventory | Where-Object own); $report.own = $own
}

if ($Mode -eq 'Verify') {
    if (-not $script:Tool) { $script:Tool = Find-SignTool }
    $verify = foreach ($t in $own) {
        $full = Join-Path $PackageRoot $t.path
        $pe = $t.path -notmatch '\.ps1$|\.psm1$'
        # /pa: the Authenticode (default) policy; /tw: warn when not timestamped. Scripts are checked with Get-AuthenticodeSignature only.
        $v = if ($pe) { Invoke-SignTool @('verify', '/pa', '/tw', $full) } else { $null }
        $sig = Get-AuthenticodeSignature -LiteralPath $full
        [pscustomobject]@{
            path = $t.path; status = [string]$sig.Status; timestamped = [bool]$sig.TimeStamperCertificate
            signtool = if ($v) { $v.exit } else { $null }
            ok = ($sig.Status -eq 'Valid') -and [bool]$sig.TimeStamperCertificate -and ((-not $v) -or $v.exit -eq 0)
        }
    }
    $report.verify = @($verify)
    $report.allOwnSigned = (@($verify | Where-Object { -not $_.ok }).Count -eq 0) -and ($verify.Count -gt 0)
}

if (-not $ReportPath) { $ReportPath = Join-Path $PackageRoot $(if ($Mode -eq 'Inventory') { 'signing-inventory.json' } else { 'signing-report.json' }) }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8

$ownSigned = @($own | Where-Object status -eq 'Valid').Count
Write-Host ("D-ASK files: {0} ({1} validly signed). Third-party files: {2} ({3} signed by their publishers, {4} unsigned)." -f $own.Count, $ownSigned, $third.Count, @($third | Where-Object status -eq 'Valid').Count, @($third | Where-Object status -eq 'NotSigned').Count)
Write-Host "Report: $ReportPath"
if ($Mode -eq 'Verify' -and -not $report.allOwnSigned) { Write-Host 'NOT SIGNED: at least one D-ASK file has no valid timestamped signature.'; exit 1 }
exit 0
