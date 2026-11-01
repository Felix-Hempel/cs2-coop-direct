<#
.SYNOPSIS
  Builds Skyline Together Direct and deploys it into the local Mods folder (Windows, PowerShell 5.1).

.DESCRIPTION
  1. tools/build.sh --game-dir <GameDir> --deploy (Git Bash; build, API check, tests, deploy)
  2. checks that SkylineTogether.dll arrived in Mods\SkylineTogetherDirect
  3. creates the inbound firewall rule for TCP 27015 from the Tailscale range (replaces a rule of the same name;
     one UAC prompt when this window is not elevated). Skip with -SkipFirewall.
  4. warns when another co-op mod is installed: "Multiplayer Co-op" (Paradox Mods ID 159499), the original
     "Skyline Together" (Paradox Mods ID 163578) or a local Mods\SkylineTogether folder; then reports enabled
     firewall rules that block Cities2.exe (deletes nothing)
  5. prints the build id (first 12 hex digits of the SHA-256 of the DLL); both players must see the same one

  powershell -ExecutionPolicy Bypass -File tools\deploy-windows.ps1 [-GameDir "D:\games\Cities Skylines II"] [-SkipFirewall] [-BashPath "C:\Program Files\Git\bin\bash.exe"]
#>
param(
    [string]$GameDir = 'D:\games\Cities Skylines II',
    [switch]$SkipFirewall,
    [string]$BashPath = ''
)

$ErrorActionPreference = 'Stop'

# a trailing backslash would swallow the closing quote of the native argument
$GameDir = $GameDir.TrimEnd('\')

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
if ($BashPath) { $bash = $BashPath } else { $bash = Find-GitBash }
if ($bash -and -not (Test-Path -LiteralPath $bash)) { throw ('bash not found: {0}' -f $bash) }
if (-not $bash) { throw 'Git Bash not found (winget install Git.Git) or pass -BashPath; a bash on the PATH may be WSL and is not used' }
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
            Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
            New-NetFirewallRule -DisplayName $RuleName -Direction Inbound -Protocol TCP -LocalPort $Port -RemoteAddress '100.64.0.0/10' -Action Allow | Out-Null
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
        $portFilter = $rule | Get-NetFirewallPortFilter
        $addrFilter = $rule | Get-NetFirewallAddressFilter
        $portOk = (@($portFilter.LocalPort) -contains [string]$Port) -and ($portFilter.Protocol -eq 'TCP')
        $addrOk = (@($addrFilter.RemoteAddress) -contains '100.64.0.0/10') -or (@($addrFilter.RemoteAddress) -contains '100.64.0.0-100.127.255.255') -or (@($addrFilter.RemoteAddress) -contains '100.64.0.0/255.192.0.0')
        if ($portOk -and $addrOk -and ([string]$rule.Enabled -eq 'True')) {
            Write-Host ('ok: rule {0} exists (TCP {1} from 100.64.0.0/10, enabled)' -f $RuleName, $Port)
        } else {
            Write-Warning ('rule {0} is stale (port: {1}, remote: {2}, enabled: {3}); rerun this script as administrator' -f $RuleName, ($portFilter.LocalPort -join ','), ($addrFilter.RemoteAddress -join ','), $rule.Enabled)
        }
    } else {
        Write-Warning ('rule {0} does not exist. Only the host needs it. Run this script again from an elevated PowerShell (or accept the UAC prompt).' -f $RuleName)
    }
}

# ---------- 4. other co-op mods ----------
# The fork has the original's assembly name (SkylineTogether.dll) and settings file: only one of them may be loaded.
Write-Step 'Check for other co-op mods'
$conflicts = 0
$pdxMods = Join-Path $UserData '.cache\Mods\pdx_mods'
$coop = @(Get-ChildItem -Path $pdxMods -Filter '159499_*' -Directory -ErrorAction SilentlyContinue)
if ($coop.Count -gt 0) {
    $conflicts++
    Write-Warning ('The old mod "Multiplayer Co-op" (Paradox Mods ID 159499) is subscribed: {0}' -f $coop[0].FullName)
    Write-Warning 'Disable it in the game under Paradox Mods (or remove it from the playset), then restart the game.'
}
$original = @(Get-ChildItem -Path $pdxMods -Filter '163578_*' -Directory -ErrorAction SilentlyContinue)
if ($original.Count -gt 0) {
    $conflicts++
    Write-Warning ('The original "Skyline Together" (Paradox Mods ID 163578) is subscribed: {0}' -f $original[0].FullName)
    Write-Warning 'Unsubscribe or disable it in the game under Paradox Mods, then restart the game. It has the same assembly name and settings file as this fork.'
}
$localOriginal = Join-Path $UserData 'Mods\SkylineTogether'
if (Test-Path -LiteralPath $localOriginal -PathType Container) {
    $conflicts++
    Write-Warning ('A local copy of the original "Skyline Together" is installed: {0}' -f $localOriginal)
    Write-Warning 'Close the game, then delete or move that folder out of Mods. It has the same assembly name and settings file as this fork.'
}
if ($conflicts -eq 0) { Write-Host 'ok: neither Multiplayer Co-op (159499), the original Skyline Together (163578) nor Mods\SkylineTogether found' }

# ---------- 4b. firewall block rules for the game ----------
# A "Cancel" in the Windows firewall dialog for Cities2.exe leaves a block rule that beats the allow rule above.
# Reported only; this script deletes nothing.
Write-Step 'Check for firewall rules that block Cities2.exe'
try {
    $blockRules = @(Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue |
        Where-Object { $_.Program -like '*Cities2.exe' } |
        Get-NetFirewallRule -ErrorAction SilentlyContinue |
        Where-Object { ([string]$_.Action -eq 'Block') -and ([string]$_.Enabled -eq 'True') })
    if ($blockRules.Count -gt 0) {
        foreach ($r in $blockRules) {
            Write-Warning ('firewall rule "{0}" ({1}) blocks Cities2.exe' -f $r.DisplayName, $r.Direction)
            Write-Warning ('  remove it in an elevated PowerShell: Remove-NetFirewallRule -Name ''{0}''  (or in "Windows Defender Firewall with Advanced Security")' -f $r.Name)
        }
    } else {
        Write-Host 'ok: no enabled block rule for Cities2.exe'
    }
} catch {
    Write-Warning ('could not read the firewall rules: {0}' -f $_.Exception.Message)
}

# ---------- 5. build id ----------
$hash = (Get-FileHash -LiteralPath $ModDll -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ''
Write-Host ('Build ID: {0}' -f $hash.Substring(0, 12)) -ForegroundColor Green
Write-Host 'Both players must have the same build ID (shown at the bottom of the mod panel).'
