<#
  Code-signing policy test (2026-11-15). Builds certificates IN MEMORY (exported to PFX files in a temp folder; nothing is added to any
  certificate store) and checks that Sign-D-ASK.ps1 refuses every one that is not publicly trusted code signing, signing nothing:
    1. a self-signed code-signing certificate
    2. a code-signing leaf issued by a private (untrusted) root
    3. a publicly-irrelevant leaf without the Code Signing usage
  Then Verify mode must report the unsigned package as NOT SIGNED (exit 1). The positive path needs a real certificate (BLOCKED here).
  Run: powershell -NoProfile -ExecutionPolicy Bypass -File tests\package\sign-policy.ps1 [-OutDir <folder>]
#>
param([string]$OutDir)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sign = Join-Path $root 'scripts\package\Sign-D-ASK.ps1'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("dask-sign-" + [guid]::NewGuid().ToString('N'))
$pkg = Join-Path $tmp 'pkg'
New-Item -ItemType Directory -Force (Join-Path $pkg 'src\hud-native\target\release') | Out-Null
# A real (unsigned) D-ASK executable and a script, so signing would really change them.
$ocr = Join-Path $root 'src\hud-native\target\release\dask-ocr.exe'
Copy-Item $ocr (Join-Path $pkg 'src\hud-native\target\release\dask-ocr.exe')
Copy-Item (Join-Path $root 'scripts\package\Install-D-ASK.ps1') (Join-Path $pkg 'Install-D-ASK.ps1')
$before = @{}; Get-ChildItem $pkg -Recurse -File | ForEach-Object { $before[$_.FullName] = (Get-FileHash $_.FullName).Hash }

$pass = 0; $fail = 0
function Check($ok, $label, $detail) { if ($ok) { $script:pass++; Write-Host "  PASS: $label" } else { $script:fail++; Write-Host "  FAIL: $label | $detail" } }

Add-Type -AssemblyName System.Security
$X = [System.Security.Cryptography.X509Certificates.X509Certificate2]
function New-Req([string]$subject) {
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    $req = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest($subject, $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    return @{ req = $req; key = $rsa }
}
function Add-Eku($req, [string[]]$oids) {
    $c = New-Object System.Security.Cryptography.OidCollection
    foreach ($o in $oids) { $c.Add([System.Security.Cryptography.Oid]::new($o)) | Out-Null }
    $req.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($c, $false)))
}
$pw = 'test-only-' + [guid]::NewGuid().ToString('N')
function Save-Pfx($cert, $name) { $p = Join-Path $tmp "$name.pfx"; [IO.File]::WriteAllBytes($p, $cert.Export('Pfx', $pw)); $p }

# 1. self-signed code signing
$s = New-Req 'CN=D-ASK Test Self-Signed'; Add-Eku $s.req @('1.3.6.1.5.5.7.3.3')
$selfPfx = Save-Pfx ($s.req.CreateSelfSigned((Get-Date).AddDays(-1), (Get-Date).AddDays(30))) 'self'
# 2. private root -> code-signing leaf
$r = New-Req 'CN=D-ASK Test Private Root'
$r.req.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($true, $false, 0, $true)))
$rootCert = $r.req.CreateSelfSigned((Get-Date).AddDays(-2), (Get-Date).AddDays(60))
$l = New-Req 'CN=D-ASK Test Leaf'; Add-Eku $l.req @('1.3.6.1.5.5.7.3.3')
$serial = [byte[]](1..8)
$leaf = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($l.req.Create($rootCert, (Get-Date).AddDays(-1), (Get-Date).AddDays(30), $serial), $l.key)
$leafPfx = Save-Pfx $leaf 'private-leaf'
# 3. leaf without Code Signing usage (server auth only)
$n = New-Req 'CN=D-ASK Test Server'; Add-Eku $n.req @('1.3.6.1.5.5.7.3.1')
$noEku = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($n.req.Create($rootCert, (Get-Date).AddDays(-1), (Get-Date).AddDays(30), [byte[]](9..16)), $n.key)
$noEkuPfx = Save-Pfx $noEku 'no-eku'

$env:DASK_SIGN_PFX_PASSWORD = $pw
$ErrorActionPreference = 'Continue'   # the refusals write to stderr on purpose
try {
    foreach ($case in @(@{ pfx = $selfPfx; label = 'a self-signed certificate'; want = 'self-signed' },
                        @{ pfx = $leafPfx; label = 'a leaf from a private root'; want = 'trusted root|not in Microsoft' },
                        @{ pfx = $noEkuPfx; label = 'a certificate without the Code Signing usage'; want = 'Code Signing usage' })) {
        $rep = Join-Path $tmp ("report-" + [guid]::NewGuid().ToString('N') + '.json')
        $null = & powershell -NoProfile -ExecutionPolicy Bypass -File $sign -PackageRoot $pkg -Mode Sign -PfxPath $case.pfx -ReportPath $rep 2>&1
        $code = $LASTEXITCODE
        $j = Get-Content $rep -Raw | ConvertFrom-Json
        $why = ($j.refused -join ' | ')
        Check ($code -eq 2 -and $why -match $case.want) "refuses $($case.label) (exit $code)" $why
    }
    $changed = @(Get-ChildItem $pkg -Recurse -File | Where-Object { $before[$_.FullName] -ne (Get-FileHash $_.FullName).Hash })
    Check ($changed.Count -eq 0) 'nothing in the package was changed by a refused signing' ($changed.FullName -join ', ')
    Check (@(Get-ChildItem $pkg -Recurse -File -Include *.exe, *.ps1 | Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -ne 'NotSigned' }).Count -eq 0) 'every file is still unsigned' ''
    $rep = Join-Path $tmp 'verify.json'
    $null = & powershell -NoProfile -ExecutionPolicy Bypass -File $sign -PackageRoot $pkg -Mode Verify -ReportPath $rep 2>&1
    $code = $LASTEXITCODE
    $j = Get-Content $rep -Raw | ConvertFrom-Json
    Check ($code -eq 1 -and $j.allOwnSigned -eq $false -and @($j.verify).Count -eq 2) 'Verify reports the unsigned package as NOT SIGNED (exit 1)' "exit $code"
    $null = & powershell -NoProfile -ExecutionPolicy Bypass -File $sign -PackageRoot $pkg -Mode Sign -ReportPath (Join-Path $tmp 'none.json') 2>&1
    Check ($LASTEXITCODE -ne 0) 'Sign without any certificate fails' "exit $LASTEXITCODE"
} finally {
    Remove-Item Env:\DASK_SIGN_PFX_PASSWORD -ErrorAction SilentlyContinue
    if ($OutDir) { New-Item -ItemType Directory -Force $OutDir | Out-Null; Get-ChildItem $tmp -Filter *.json | Copy-Item -Destination $OutDir }
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
Write-Host "`n$pass passed, $fail failed"
if ($fail) { exit 1 }
