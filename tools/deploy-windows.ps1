<#
.SYNOPSIS
  Builds Skyline Together Direct and deploys it into the local Mods folder (Windows, PowerShell 5.1).

.DESCRIPTION
  1. tools/build.sh --game-dir <GameDir> --deploy (Git Bash; build, API check, tests, deploy)
  2. checks that SkylineTogether.dll arrived in Mods\SkylineTogetherDirect
  3. creates the inbound firewall rule for TCP 27015 from the Tailscale range (replaces a rule of the same name;
     one UAC prompt when this window is not elevated). Skip with -SkipFirewall.
  4. warns when the old mod (Paradox Mods ID 159499) is still subscribed
  5. prints the build id (first 12 hex digits of the SHA-256 of the DLL); both players must see the same one

  powershell -ExecutionPolicy Bypass -File tools\deploy-windows.ps1 [-GameDir "D:\games\Cities Skylines II"] [-SkipFirewall]
#>
param(
    [string]$GameDir = 'D:\games\Cities Skylines II',
    [switch]$SkipFirewall
)

$ErrorActionPreference = 'Stop'

$RuleName = 'SkylineTogetherDirect'
$Port = 27015
$UserData = Join-Path $env:USERPROFILE 'AppData\LocalLow\Colossal Order\Cities Skylines II'
$ModDll = Join-Path $UserData 'Mods\SkylineTogetherDirect\SkylineTogether.dll'
$RepoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host ('== {0}' -f $text) -ForegroundColor Cyan
}

# Git Bash, explicitly: a "bash" on the PATH may be the WSL launcher, which cannot run the build
function Find-GitBash {
    $candidates = @()
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
        if ($base) { $candidates += (Join-Path $base 'Git\bin\bash.exe') }
    }
    $git = Get-Command git.exe -ErrorAction SilentlyContinue
    if ($git) {
        $gitRoot = Split-Path -Parent (Split-Path -Parent $git.Source)
        $candidates += (Join-Path $gitRoot 'bin\bash.exe')
    }
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }
    return $null
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---------- 1. build + deploy ----------
Write-Step 'Build and deploy (tools/build.sh --deploy)'
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'Cities2_Data\Managed\Game.dll'))) {
    throw ('Game.dll not found below "{0}": pass -GameDir with the install folder of Cities: Skylines II' -f $GameDir)
}
$bash = Find-GitBash
if (-not $bash) { throw 'Git Bash not found (winget install Git.Git); a bash on the PATH may be WSL and is not used' }
Write-Host ('bash: {0}' -f $bash)

Push-Location $RepoRoot
try {
    & $bash 'tools/build.sh' '--game-dir' $GameDir '--deploy'
    $buildExit = $LASTEXITCODE
} finally {
    Pop-Location
}
if ($buildExit -ne 0) { throw ('tools/build.sh failed (exit code {0}); nothing else was done' -f $buildExit) }

# ---------- 2. deployed? ----------
Write-Step 'Check the deployed mod'
if (-not (Test-Path -LiteralPath $ModDll)) { throw ('missing after deploy: {0}' -f $ModDll) }
Write-Host ('ok: {0}' -f $ModDll)

# ---------- 3. firewall ----------
if ($SkipFirewall) {
    Write-Step 'Firewall rule skipped (-SkipFirewall)'
} else {
    Write-Step ('Firewall rule {0} (TCP {1}, Tailscale range 100.64.0.0/10)' -f $RuleName, $Port)
    $ruleCmd = ("Get-NetFirewallRule -DisplayName '{0}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; " +
        "New-NetFirewallRule -DisplayName '{0}' -Direction Inbound -Protocol TCP -LocalPort {1} " +
        "-RemoteAddress 100.64.0.0/10 -Action Allow | Out-Null") -f $RuleName, $Port
    try {
        if (Test-Admin) {
            Invoke-Expression $ruleCmd
        } else {
            Write-Host 'not elevated: a UAC prompt follows for this one step'
            $argLine = '-NoProfile -ExecutionPolicy Bypass -Command "{0}"' -f $ruleCmd
            Start-Process -FilePath 'powershell.exe' -ArgumentList $argLine -Verb RunAs -Wait
        }
    } catch {
        Write-Warning ('firewall rule not created: {0}' -f $_.Exception.Message)
    }
    $rule = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue
    if ($rule) {
        Write-Host ('ok: rule {0} exists (enabled: {1})' -f $RuleName, $rule.Enabled)
    } else {
        Write-Warning ('rule {0} does not exist. Only the host needs it. Run this script again from an elevated PowerShell (or accept the UAC prompt).' -f $RuleName)
    }
}

# ---------- 4. old mod ----------
$oldMod = @(Get-ChildItem -Path (Join-Path $UserData '.cache\Mods\pdx_mods') -Filter '159499_*' -Directory -ErrorAction SilentlyContinue)
if ($oldMod.Count -gt 0) {
    Write-Host ''
    Write-Warning ('The old Skyline Together (Paradox Mods ID 159499) is subscribed: {0}' -f $oldMod[0].FullName)
    Write-Warning 'Disable it in the game under Paradox Mods or remove the subscription, then restart the game. Both mods at once will conflict.'
}

# ---------- 5. build id ----------
$hash = (Get-FileHash -LiteralPath $ModDll -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ''
Write-Host ('Build ID: {0}' -f $hash.Substring(0, 12)) -ForegroundColor Green
Write-Host 'Both players must have the same build ID (shown at the bottom of the mod panel).'
