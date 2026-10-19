# Installs or updates DropDock on this PC. Safe to run again at any time. No admin rights needed.
#
#   irm https://raw.githubusercontent.com/myfxpaddy/dropdock/main/install/windows.ps1 | iex
#
# Downloads the latest release and runs it. DropDock.exe installs itself into
# %LOCALAPPDATA%\Programs\DropDock, adds Start menu and Desktop shortcuts, starts with Windows,
# replaces any copy that is already running, and sits in the tray.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'  # the progress bar makes Windows PowerShell download very slowly
$repo = 'myfxpaddy/dropdock'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

function Say([string]$message) { Write-Host "==> $message" }

if ([Environment]::OSVersion.Version.Major -lt 10) { throw 'DropDock needs Windows 10 or 11.' }
$framework = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
if (-not $framework -or $framework.Release -lt 528040) {
    throw '.NET Framework 4.8 is missing. Install it from https://dotnet.microsoft.com/download/dotnet-framework/net48 and run this again.'
}

$work = Join-Path $env:TEMP ('DropDock-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
$download = Join-Path $work 'DropDock.exe'

Say 'Downloading DropDock'
try {
    Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/$repo/releases/latest/download/DropDock.exe" -OutFile $download
}
catch {
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        gh release download --repo $repo --pattern DropDock.exe --dir $work --clobber
        if ($LASTEXITCODE -ne 0) { throw 'Could not download DropDock with the GitHub CLI. Run gh auth login and try again.' }
    }
    else {
        throw "Could not download DropDock. If the repository is private, install the GitHub CLI (winget install GitHub.cli), run 'gh auth login', and try again."
    }
}
# Downloads carry a "came from the internet" mark that makes SmartScreen ask. This is your own app.
Unblock-File -Path $download

Say 'Installing'
$installed = Join-Path $env:LOCALAPPDATA 'Programs\DropDock\DropDock.exe'
Start-Process -FilePath $download
$running = $null
for ($i = 0; $i -lt 30 -and -not $running; $i++) {
    Start-Sleep -Seconds 1
    $running = Get-Process DropDock -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installed }
}
if (-not $running) {
    throw "DropDock did not start. Run $installed yourself and look for a SmartScreen or antivirus message."
}
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

Say "DropDock is installed and running ($installed)"
Write-Host ''
Write-Host '  1. If Windows Defender Firewall asks, tick "Private networks" and click "Allow access".'
Write-Host '  2. Pin it: open Start, right-click DropDock, choose Pin to taskbar.'
Write-Host '  3. Open the bar: rest the pointer at the top edge of the screen, or click the tray icon.'
Write-Host '  4. Click one of your other computers marked "Click to link", check both show the same'
Write-Host '     six-digit code, and click Allow on the other computer.'
Write-Host ''

$public = Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object { $_.NetworkCategory -eq 'Public' }
if ($public) {
    Write-Host "Note: the network '$($public[0].Name)' is set to Public. DropDock needs it set to Private (see INSTALL.md)." -ForegroundColor Yellow
}
$defender = Get-MpPreference -ErrorAction SilentlyContinue
if ($defender -and $defender.EnableControlledFolderAccess -eq 1) {
    Write-Host 'Note: Controlled folder access is on, which stops DropDock saving into Documents (see INSTALL.md).' -ForegroundColor Yellow
}
Write-Host "Log: $env:APPDATA\DropDock\dropdock.log"
