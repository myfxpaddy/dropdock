# Installing DropDock

This guide takes a computer from nothing to a working, linked DropDock. It is written so that
Claude Code (or a person) can follow it top to bottom on a Mac or a Windows PC.

DropDock has to be installed on every computer that should send or receive. Each computer needs
this once; after that it starts by itself and finds the others automatically.

* [Fastest way: give Claude Code a prompt](#fastest-way-give-claude-code-a-prompt)
* [Mac](#mac)
* [Windows](#windows)
* [Linking computers](#linking-computers)
* [Checking it works](#checking-it-works)
* [Troubleshooting](#troubleshooting)
* [Updating and uninstalling](#updating-and-uninstalling)
* [Where everything lives](#where-everything-lives)

## Fastest way: give Claude Code a prompt

Open Claude Code on the new computer and paste the prompt for that kind of computer. The same
prompts are in DropDock itself: click the **i** at the end of the bar, or choose
**Set Up Another Computer** from the DropDock menu, and use **Copy Prompt**.

### Prompt for a Mac

```text
Install DropDock on this Mac for me. DropDock is my own app for sending files and clipboard text between my computers on the same Wi-Fi. The source code and the full setup guide are at https://github.com/myfxpaddy/dropdock (INSTALL.md, Mac section). Follow these steps start to finish:

1. Install it by running this in the terminal:
   curl -fsSL https://raw.githubusercontent.com/myfxpaddy/dropdock/main/install/mac.sh | bash
   If that fails with a 404 (the repo is private), run `gh auth login` and ask me to finish the browser login, then run:
   gh api repos/myfxpaddy/dropdock/contents/install/mac.sh -H "Accept: application/vnd.github.raw" | bash

2. While it starts, tell me to click Allow when macOS asks whether DropDock may find devices on the local network, and when it asks about the Documents and Desktop folders. These prompts appear on screen, not in the terminal.

3. Check it worked: confirm `pgrep -fl DropDock.app` shows it running, confirm DropDock is in the Dock and that ~/Desktop/DropDock exists, and show me the last 10 lines of ~/Library/Application Support/DropDock/dropdock.log. Lines starting with "found" mean it can see my other computers.

4. Tell me how to link it: open the DropDock bar on this Mac by resting the pointer at the top edge of a screen (or clicking DropDock in the Dock), click one of my other computers marked "Click to link", check both computers show the same six-digit code, and click Allow on the other computer.

5. If it cannot see my other computers after a minute, work through the Troubleshooting section of INSTALL.md with me.

Do not change any other settings on this computer without asking me first.
```

### Prompt for a Windows PC

```text
Install DropDock on this Windows PC for me. DropDock is my own app for sending files and clipboard text between my computers on the same Wi-Fi. The source code and the full setup guide are at https://github.com/myfxpaddy/dropdock (INSTALL.md, Windows section). Follow these steps start to finish:

1. Install it by running this in PowerShell (a normal window is fine, no admin needed):
   irm https://raw.githubusercontent.com/myfxpaddy/dropdock/main/install/windows.ps1 | iex
   If that fails with a 404 (the repo is private), install the GitHub CLI if needed (winget install GitHub.cli), run `gh auth login` and ask me to finish the browser login, then run:
   gh api repos/myfxpaddy/dropdock/contents/install/windows.ps1 -H "Accept: application/vnd.github.raw" | Out-String | iex

2. While it starts, tell me to watch for the Windows Defender Firewall window: tick "Private networks" and click "Allow access". Without this my other computers cannot reach this PC. If I missed it, follow the firewall step in INSTALL.md (it needs one admin approval from me).

3. Check the network: run Get-NetConnectionProfile. If the Wi-Fi shows NetworkCategory Public, explain that DropDock needs it set to Private and ask me before changing it.

4. Check it worked: confirm Get-Process DropDock shows it running, confirm "$env:LOCALAPPDATA\Programs\DropDock\DropDock.exe" exists, and show me the last 10 lines of "$env:APPDATA\DropDock\dropdock.log". Lines starting with "found" mean it can see my other computers.

5. Tell me how to pin it: open Start, right-click DropDock, choose Pin to taskbar.

6. Tell me how to link it: open the DropDock bar by resting the pointer at the top edge of the screen (or clicking the DropDock tray icon), click one of my other computers marked "Click to link", check both computers show the same six-digit code, and click Allow on the other computer.

7. If it cannot see my other computers after a minute, work through the Troubleshooting section of INSTALL.md with me.

Do not change any other settings on this computer without asking me first.
```

## Mac

Needs macOS 13 (Ventura) or newer. One app works on Apple Silicon and Intel.

### 1. Install

In Terminal:

```sh
curl -fsSL https://raw.githubusercontent.com/myfxpaddy/dropdock/main/install/mac.sh | bash
```

The script ([install/mac.sh](install/mac.sh)):

1. Downloads `DropDock-mac.zip` from the latest GitHub release.
2. Stops any copy that is already running.
3. Puts `DropDock.app` in `/Applications` (or `~/Applications` if `/Applications` is not writable).
4. Clears the quarantine flag, so Gatekeeper does not block the app.
5. Opens it. On first launch DropDock registers itself to start at every login (a per-user
   LaunchAgent named `app.dropdock.agent` that also restarts it if it ever crashes), adds itself to
   the Dock, and puts a `DropDock` alias on the Desktop.
6. Checks that it is running.

If the repository is private the download needs a GitHub login. Install the GitHub CLI if it is
missing (`brew install gh`), run `gh auth login`, then:

```sh
gh api repos/myfxpaddy/dropdock/contents/install/mac.sh -H "Accept: application/vnd.github.raw" | bash
```

### 2. Answer the macOS questions

These appear on screen, not in Terminal. Click **Allow** for each:

| Question | Why |
|---|---|
| "DropDock" would like to find devices on your local network | To see your other computers |
| "DropDock" would like to access files in your Documents folder | To save files you receive in `Documents/DropDock` |
| "DropDock" would like to access files in your Desktop folder | To put the alias on the Desktop |
| "DropDock" wants to paste from another app (newer macOS, when you first click Paste) | To read what you copied |

If one was declined by mistake: **System Settings > Privacy & Security**, then **Local Network**
(turn DropDock on) or **Files and Folders** (turn on Documents for DropDock).

### Manual install (no Terminal)

1. Download `DropDock-mac.zip` from the [latest release](https://github.com/myfxpaddy/dropdock/releases/latest) and unzip it.
2. Open DropDock. If macOS says it cannot be opened, go to **System Settings > Privacy & Security**
   and click **Open Anyway**.
3. When it offers to move itself to Applications, click **Move to Applications**.

## Windows

Needs Windows 10 or 11 and .NET Framework 4.8, which Windows 10 (version 1903 and later) and
Windows 11 already include. No admin rights are needed.

### 1. Install

In PowerShell (an ordinary window, not "Run as administrator"):

```powershell
irm https://raw.githubusercontent.com/myfxpaddy/dropdock/main/install/windows.ps1 | iex
```

The script ([install/windows.ps1](install/windows.ps1)):

1. Checks the Windows and .NET Framework versions.
2. Downloads `DropDock.exe` from the latest GitHub release and removes the "downloaded from the
   internet" mark so SmartScreen does not interrupt.
3. Runs it. `DropDock.exe` installs itself into `%LOCALAPPDATA%\Programs\DropDock`, closes any older
   copy, adds Start menu and Desktop shortcuts, registers itself to start with Windows (the
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `DropDock`), and puts its icon in the
   tray.
4. Checks that it is running, and warns if the network is set to Public or if Controlled folder
   access is on (see below).

If the repository is private: `winget install GitHub.cli`, open a new PowerShell window,
`gh auth login`, then:

```powershell
gh api repos/myfxpaddy/dropdock/contents/install/windows.ps1 -H "Accept: application/vnd.github.raw" | Out-String | iex
```

### 2. Allow it through the firewall

The first time DropDock starts, **Windows Defender Firewall** asks about it. Tick
**Private networks** and click **Allow access**. Without this, other computers cannot find or reach
this PC (it can still send to them).

If the question was dismissed or answered wrongly, add the rule from PowerShell. This asks for one
admin approval:

```powershell
$exe = "$env:LOCALAPPDATA\Programs\DropDock\DropDock.exe"
Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile', '-Command', "Get-NetFirewallApplicationFilter -Program '$exe' -ErrorAction SilentlyContinue | Get-NetFirewallRule | Where-Object Action -eq Block | Remove-NetFirewallRule; New-NetFirewallRule -DisplayName 'DropDock' -Direction Inbound -Program '$exe' -Action Allow -Profile Private,Domain"
```

### 3. Make sure the Wi-Fi is a Private network

```powershell
Get-NetConnectionProfile
```

If `NetworkCategory` is `Public`, the firewall keeps DropDock closed. Change it (ask the owner
first) in **Settings > Network & internet > Wi-Fi > (your network) > Private network**, or with one
admin approval:

```powershell
Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile', '-Command', "Set-NetConnectionProfile -InterfaceAlias '<the InterfaceAlias shown above>' -NetworkCategory Private"
```

### 4. If Controlled folder access is on

Windows Security's ransomware protection blocks unknown apps from writing into Documents. Check:

```powershell
(Get-MpPreference).EnableControlledFolderAccess   # 1 means on
```

If it is on, allow DropDock (ask the owner first; needs one admin approval):

```powershell
$exe = "$env:LOCALAPPDATA\Programs\DropDock\DropDock.exe"
Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile', '-Command', "Add-MpPreference -ControlledFolderAccessAllowedApplications '$exe'"
```

### 5. Pin it to the taskbar

Windows does not let apps pin themselves. Open **Start**, right-click **DropDock**, choose
**Pin to taskbar**. Clicking the pinned icon opens the bar. The Desktop shortcut works too, and
dropping files on it opens the bar so you can pick a computer.

### Manual install (no PowerShell)

Download `DropDock.exe` from the [latest release](https://github.com/myfxpaddy/dropdock/releases/latest)
and double-click it. If SmartScreen says "Windows protected your PC", click **More info**, then
**Run anyway**. Then do steps 2 to 5 above.

## Linking computers

New installs do not trust each other yet, so nobody else on the same Wi-Fi can send you anything.
Link each new computer once:

1. On the new computer, open the bar: rest the pointer at the top edge of a screen (a screen with no
   other screen above it), or click DropDock in the Dock, tray or Desktop.
2. Your other computers appear dimmed with **Click to link**. Click one.
3. Both computers show the same six-digit code. On the other computer, check the code matches and
   click **Allow**.

The new computer joins that computer's group, and every computer in the group sees it. If the
automatic way cannot reach the other computer, use the DropDock menu: **Link Key > Copy Link Key**
on a linked computer, then **Link Key > Enter a Link Key** on the new one.

## Checking it works

| | Mac | Windows |
|---|---|---|
| Running | `pgrep -fl DropDock.app` | `Get-Process DropDock` |
| Starts at login | `launchctl print gui/$(id -u)/app.dropdock.agent` | `Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run DropDock` |
| Log | `tail -20 ~/Library/Application\ Support/DropDock/dropdock.log` | `Get-Content "$env:APPDATA\DropDock\dropdock.log" -Tail 20` |
| Can reach another computer | `nc -vz <its IP> 47821` | `Test-NetConnection <its IP> -Port 47821` |

In the log, `found <name> at <ip>:<port>` means DropDock can see that computer. `linked=true` on a
Mac (or the computer appearing in the bar without "Click to link") means they are linked.

## Troubleshooting

**The other computers never appear.**

1. Both computers must be on the same network. Guest Wi-Fi and some office or hotel networks keep
   devices apart ("client isolation"); DropDock cannot work across that.
2. A VPN can route traffic away from the local network. Try with it off.
3. Mac: **System Settings > Privacy & Security > Local Network** must have DropDock turned on.
4. Windows: the firewall rule (step 2) and a Private network (step 3).
5. Check the log on both sides for `found` lines and test the port with the commands above.
   Discovery uses UDP port 47820 (broadcast); transfers use TCP port 47821.

**"Not linked with this computer" or "has a different link key".** Link the two computers again
(see [Linking computers](#linking-computers)).

**Received files do not appear.** They go to `Documents/DropDock` (`Documents\DropDock` on
Windows). On a Mac, check Documents access in **Privacy & Security > Files and Folders**. On Windows,
check Controlled folder access (step 4).

**The bar does not open at the top.** Resting the pointer only works on a top edge with no screen
above it. On a laptop with a monitor arranged above it, use the top of the monitor, or click DropDock
in the Dock or tray. **Open Bar When Pointer Rests at the Top Edge** in the menu must be on.

**macOS asks for permissions again after an update.** Expected: DropDock is not signed with a paid
Apple developer certificate, so macOS treats each new build as a new app. Click Allow again.

## Updating and uninstalling

**Update:** run the same install command again. It replaces the app and keeps your settings and
links.

**Uninstall on a Mac:**

```sh
launchctl bootout gui/$(id -u)/app.dropdock.agent 2>/dev/null
rm -f ~/Library/LaunchAgents/app.dropdock.agent.plist ~/Desktop/DropDock
rm -rf /Applications/DropDock.app ~/Applications/DropDock.app "$HOME/Library/Application Support/DropDock"
```

Then remove it from the Dock (drag it out). Received files stay in `Documents/DropDock`.

**Uninstall on Windows:** right-click the tray icon, **Uninstall DropDock**. Received files stay in
`Documents\DropDock`.

## Where everything lives

| | Mac | Windows |
|---|---|---|
| App | `/Applications/DropDock.app` | `%LOCALAPPDATA%\Programs\DropDock\DropDock.exe` |
| Settings and link key | `~/Library/Application Support/DropDock/state.json` | `%APPDATA%\DropDock\state.json` |
| Log | `~/Library/Application Support/DropDock/dropdock.log` | `%APPDATA%\DropDock\dropdock.log` |
| Received files | `~/Documents/DropDock` | `Documents\DropDock` |
| Start at login | `~/Library/LaunchAgents/app.dropdock.agent.plist` | `HKCU\...\CurrentVersion\Run\DropDock` |
| Ports | UDP 47820, TCP 47821 | UDP 47820, TCP 47821 |

The link key in `state.json` is what lets your computers trust each other. Do not share it.
