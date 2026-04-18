<#
.SYNOPSIS
    Configure the Windows host to reach a FeatBit deployment running inside WSL2.

.DESCRIPTION
    After Quickstart-WSL.ps1 completes inside WSL, nginx is running inside WSL
    and listening on 0.0.0.0:80. WSL2 already bridges Windows' localhost to
    that port, so the only thing missing on Windows is hosts-file entries for
    the featbit.*.local / featbit-api.*.local / etc. names that nginx routes by.

    This script adds (or removes) those entries in
    C:\Windows\System32\drivers\etc\hosts, then sanity-tests each HTTP URL.
    The managed block is fenced with markers so re-runs update in place
    rather than appending duplicates.

    Run this from Windows PowerShell (not from inside WSL). It must run as
    Administrator because the hosts file is writable only to admins — the
    script will self-elevate if needed.

.PARAMETER Remove
    Remove the managed block from the hosts file instead of adding/updating it.

.PARAMETER SkipTest
    Skip the post-configuration HTTP reachability test.

.EXAMPLE
    pwsh -ExecutionPolicy Bypass -File \\wsl.localhost\Ubuntu\home\<user>\...\Configure-WindowsHostAccess.ps1
    Add hosts entries and verify each URL responds. -ExecutionPolicy Bypass is
    required when launching the script from a WSL UNC share, since PowerShell
    treats those paths as remote.

.EXAMPLE
    pwsh -ExecutionPolicy Bypass -File ...\Configure-WindowsHostAccess.ps1 -Remove
    Strip the managed block out of the hosts file.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Remove,
    [switch]$SkipTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($IsLinux) {
    Write-Host ""
    Write-Host "  x Configure-WindowsHostAccess.ps1 must be run from Windows PowerShell." -ForegroundColor Red
    Write-Host "  You appear to be inside WSL — re-run from a Windows PowerShell window." -ForegroundColor Gray
    Write-Host ""
    exit 1
}

# ── Console helpers ───────────────────────────────────────────────────────────

function Write-Step    { param([string]$M) Write-Host "`n============================================================" -ForegroundColor DarkCyan
                         Write-Host "  $M" -ForegroundColor Cyan
                         Write-Host "============================================================" -ForegroundColor DarkCyan }
function Write-Success { param([string]$M) Write-Host "  [OK] $M" -ForegroundColor Green }
function Write-Info    { param([string]$M) Write-Host "  $M" -ForegroundColor Gray }
function Write-Warn    { param([string]$M) Write-Host "  !! $M" -ForegroundColor Yellow }
function Write-Fail    { param([string]$M) Write-Host "  x  $M" -ForegroundColor Red }

# ── Elevation (Windows) ───────────────────────────────────────────────────────

function Test-Administrator {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [System.Security.Principal.WindowsPrincipal]$id
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    Write-Warn "This script must run as Administrator to modify the hosts file."
    Write-Info "Re-launching elevated..."
    # ExecutionPolicy Bypass is required because the script often lives on a
    # WSL UNC share (\\wsl.localhost\...), which PowerShell treats as remote.
    $argList = @("-NoExit", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
    if ($Remove)   { $argList += "-Remove" }
    if ($SkipTest) { $argList += "-SkipTest" }
    Start-Process pwsh -Verb RunAs -ArgumentList $argList
    Write-Info "Close this window and continue in the new elevated terminal."
    exit 0
}

# ── Configuration ─────────────────────────────────────────────────────────────

$HostsFile = "$env:SystemRoot\System32\drivers\etc\hosts"
$BeginMarker = "# BEGIN featbit-wsl-access"
$EndMarker   = "# END featbit-wsl-access"

# Must match the DNS names that Setup-FeatBitProxy.ps1 configures in nginx.
# We probe the root path of each host — any HTTP status means nginx routed to
# the upstream successfully. Only network-level failures (timeout, refused,
# DNS) count as broken reachability.
$WestHttpHosts = @(
    @{ Name = "featbit.west.local";       Url = "http://featbit.west.local/" }
    @{ Name = "featbit-api.west.local";   Url = "http://featbit-api.west.local/" }
    @{ Name = "featbit-eval.west.local";  Url = "http://featbit-eval.west.local/" }
    @{ Name = "featbit-kafka.west.local"; Url = "http://featbit-kafka.west.local/" }
)
$EastHttpHosts = @(
    @{ Name = "featbit.east.local";       Url = "http://featbit.east.local/" }
    @{ Name = "featbit-api.east.local";   Url = "http://featbit-api.east.local/" }
    @{ Name = "featbit-eval.east.local";  Url = "http://featbit-eval.east.local/" }
    @{ Name = "featbit-kafka.east.local"; Url = "http://featbit-kafka.east.local/" }
)
# TCP-only — no HTTP test, but still needs hosts entry for kubectl proxying.
$TcpOnlyHosts = @("redis.west.local", "redis.east.local")

$AllHostNames = @()
$AllHostNames += $WestHttpHosts | ForEach-Object { $_.Name }
$AllHostNames += $EastHttpHosts | ForEach-Object { $_.Name }
$AllHostNames += $TcpOnlyHosts

# ── Hosts-file edit ───────────────────────────────────────────────────────────

function Remove-Block([string[]]$Lines) {
    # Return $Lines with any existing managed block stripped out.
    $out = New-Object System.Collections.Generic.List[string]
    $inside = $false
    foreach ($line in $Lines) {
        if ($line -match [regex]::Escape($BeginMarker)) { $inside = $true;  continue }
        if ($line -match [regex]::Escape($EndMarker))   { $inside = $false; continue }
        if (-not $inside) { $out.Add($line) }
    }
    # Trim any trailing blank lines produced by the removal.
    while ($out.Count -gt 0 -and [string]::IsNullOrWhiteSpace($out[$out.Count - 1])) {
        $out.RemoveAt($out.Count - 1)
    }
    return ,$out.ToArray()
}

Write-Step "Configure-WindowsHostAccess — $(if ($Remove) { 'REMOVE' } else { 'UPDATE' })"
Write-Info "Hosts file: $HostsFile"

if (-not (Test-Path $HostsFile)) { throw "Hosts file not found at $HostsFile" }

$original = Get-Content $HostsFile
$stripped = Remove-Block $original

if ($Remove) {
    if (@($original).Count -eq @($stripped).Count) {
        Write-Info "No managed block found — nothing to remove."
    } else {
        if ($PSCmdlet.ShouldProcess($HostsFile, "Remove managed block")) {
            Set-Content -Path $HostsFile -Value $stripped -Encoding ASCII
            Write-Success "Managed block removed."
        }
    }
    return
}

# Build new block.
$newBlock = New-Object System.Collections.Generic.List[string]
$newBlock.Add($BeginMarker)
$newBlock.Add("# Managed by Configure-WindowsHostAccess.ps1 — do not edit by hand.")
$newBlock.Add("# Maps FeatBit DNS names to 127.0.0.1 so Windows can reach the nginx")
$newBlock.Add("# running inside WSL2 (Windows localhost is bridged to WSL by WSL2).")
foreach ($n in $AllHostNames) {
    $newBlock.Add(("127.0.0.1`t{0}" -f $n))
}
$newBlock.Add($EndMarker)

$final = @()
$final += $stripped
if ($final.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($final[$final.Count - 1])) { $final += "" }
$final += $newBlock.ToArray()

if ($PSCmdlet.ShouldProcess($HostsFile, "Write managed FeatBit host block")) {
    Set-Content -Path $HostsFile -Value $final -Encoding ASCII
    Write-Success "Hosts file updated with $($AllHostNames.Count) entries."
}

# ── Connectivity test ─────────────────────────────────────────────────────────

if ($SkipTest) { return }

Write-Step "Connectivity test (HTTP URLs)"
Write-Info "Each URL is expected to respond — any HTTP status means nginx routed correctly."
Write-Info ""

$results = @()
foreach ($h in @($WestHttpHosts + $EastHttpHosts)) {
    $url = $h.Url
    $code = $null
    $errMsg = $null
    try {
        $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5 -MaximumRedirection 0 -ErrorAction Stop
        $code = [int]$resp.StatusCode
    }
    catch {
        # Try to extract an HTTP status from the exception. Both PS 7's
        # HttpResponseException and legacy WebException carry a Response object
        # when the server actually answered (4xx/5xx). If there's no response,
        # it was a real network failure (timeout, refused, DNS).
        $resp = $null
        if ($_.Exception.PSObject.Properties.Name -contains 'Response') { $resp = $_.Exception.Response }
        if ($resp -and $resp.PSObject.Properties.Name -contains 'StatusCode') {
            $code = [int]$resp.StatusCode
        } else {
            $errMsg = $_.Exception.Message.Split([char]10)[0]
        }
    }

    if ($null -ne $code) {
        Write-Success ("{0,-32} -> HTTP {1}" -f $h.Name, $code)
        $results += [PSCustomObject]@{ Host = $h.Name; Url = $url; Code = $code; Ok = $true }
    } else {
        Write-Fail ("{0,-32} -> {1}" -f $h.Name, $errMsg)
        $results += [PSCustomObject]@{ Host = $h.Name; Url = $url; Code = $errMsg; Ok = $false }
    }
}

Write-Info ""
$okCount   = ($results | Where-Object { $_.Ok }).Count
$failCount = ($results | Where-Object { -not $_.Ok }).Count

if ($failCount -eq 0) {
    Write-Success "All $okCount endpoints reachable."
    Write-Info ""
    Write-Info "Open in a browser:"
    Write-Info "  http://featbit.west.local/"
    Write-Info "  http://featbit.east.local/"
} else {
    Write-Warn "$failCount endpoint(s) did not respond. Common causes:"
    Write-Info "  * nginx inside WSL is not running — check 'sudo systemctl status nginx' in WSL"
    Write-Info "  * kubectl port-forwards inside WSL are down — re-run Start-PortForwards.ps1"
    Write-Info "  * Something on Windows is already using port 80 (IIS, Skype) — netstat -ano | findstr :80"
}
