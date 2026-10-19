# DropDock

Your own docking station for sending files and clipboard text between your computers on the same
Wi-Fi. It hides at the top of the screen and only appears when you need it. Works on Mac and Windows,
and Macs and PCs can send to each other.

## Using it

**Send files.** Drag any file or folder to the very top edge of the screen. The bar slides down showing
your computers. Drop onto one and it starts sending straight away. Drop onto **General** to send to
every computer at once.

**Open the bar without dragging.** Push the pointer to the top edge of the screen and rest it there
for a moment. The bar opens right under the pointer and closes when you move away. You can also click
DropDock in the Dock (Mac), the DropDock icon in the menu bar or tray, or the Desktop shortcut.

With several monitors, the top edge is the top of any screen that has no other screen above it.

**Paste to another computer.** Copy anything as normal, open the bar, click **Paste**, then click where
it should go:

* **A computer**: it lands on that computer's clipboard, ready for Command-V or Ctrl+V.
* **General**: a shared board for all your computers. On any computer, open the bar and click
  **Copy** on General.

Bold, italics, headings, lists, tables and links come across intact, because DropDock sends HTML, RTF
and plain text together and each app picks the richest one it understands. Images work too.

You can also drag selected text straight onto a computer or onto General.

**Where things go.** Received files are saved in `Documents/DropDock` on the receiving computer. If a
file with that name is already there, the new one becomes `name (2)`.

**Other ways in.** Click a computer to pick files with a file dialog. Drop files onto the DropDock
icon (Dock, Desktop shortcut or Finder) and then pick a computer.

## Setting up a computer (once)

DropDock has to be on every computer. After that it starts with the computer and needs nothing else.
**[INSTALL.md](INSTALL.md) is the full start-to-finish guide**, including prompts you can paste into
Claude Code on the new computer. The same prompts are in the app: click the **i** at the end of the bar.

One line installs or updates it:

```sh
# Mac (Terminal)
curl -fsSL https://raw.githubusercontent.com/myfxpaddy/dropdock/main/install/mac.sh | bash
```

```powershell
# Windows (PowerShell, no admin needed)
irm https://raw.githubusercontent.com/myfxpaddy/dropdock/main/install/windows.ps1 | iex
```

Or do it by hand:

### Mac (macOS 13 or newer, Apple Silicon or Intel)

1. Copy `DropDock-mac.zip` to the Mac and unzip it.
2. Open **DropDock**. When it offers to move itself to Applications, click **Move**. It adds itself to
   the Dock and puts an alias on the Desktop (turn the Dock item off with **Keep in Dock** in its menu).
3. macOS asks a few one-time questions. Answer **Allow** to each:
   * *Find devices on your local network*: needed to see your other computers.
   * *Access files in your Documents folder*: needed to save what you receive.
   * If it asks whether DropDock may *paste from other apps* (newer macOS versions, the first time
     you click Paste), allow it: that is how it reads what you copied.
4. If macOS says DropDock "cannot be opened" (because it was downloaded or AirDropped), go to
   **System Settings > Privacy & Security** and click **Open Anyway**. Copying it over with a USB
   stick, a network share, or DropDock itself avoids this.

### Windows (Windows 10 or 11)

1. Copy `DropDock.exe` to the PC (anywhere: Downloads, Desktop, a USB stick).
2. Double-click it. It installs itself for your user account (no admin needed), adds Start menu and
   Desktop shortcuts, starts with Windows, and puts its icon in the tray.
3. When **Windows Defender Firewall** asks, tick **Private networks** and click **Allow access**.
   Without this, other computers cannot find or reach this one. If your Wi-Fi is set to *Public*,
   either change it to *Private* (Settings > Network > Wi-Fi > your network) or tick Public too.
4. If SmartScreen says "Windows protected your PC" (because the file was downloaded), click
   **More info > Run anyway**. Copying it over with a USB stick or with DropDock avoids this.
5. To pin it: open Start, right-click DropDock, **Pin to taskbar**.

### Linking your computers

New installs do not trust each other yet, so a stranger on a cafe Wi-Fi can never send you anything.

1. On the new computer, open the bar. Your other computers show dimmed with **Click to link**.
2. Click the computer you want to join. Both screens show the same six-digit code.
3. On the other computer, check the code matches and click **Allow**.

That is it. Every computer you link the same way joins the same group, and they all see each other.
If the automatic way cannot reach a computer, use **Link Key > Copy Link Key** on one computer and
**Link Key > Enter a Link Key** on the other.

## Light on resources

* Native on each system: Swift and AppKit on Mac (1.6 MB app), .NET Framework 4.8 on Windows
  (one 280 KB exe; .NET 4.8 is already part of Windows 10 and 11).
* No polling while idle. It finds computers with a tiny broadcast every 50 seconds, and only wakes
  when a packet arrives, a mouse button is held, or the pointer reaches the top edge.
* The bar is only drawn while it is open.

## Security

* Computers only talk after proving they share the same 100-bit group key. Linking uses an elliptic
  curve key exchange with a six-digit check code, so nobody on the network can join silently.
* Everything sent between computers is encrypted (AES-256 with HMAC-SHA256 authentication).
* DropDock only accepts connections from local network addresses.
* Received file names are cleaned so nothing can be written outside `Documents/DropDock`.

## Menu options

* **Start at Login / Start with Windows** (on by default)
* **Put Clips Sent Here on the Clipboard** (on by default; turn off to get a "Copy" button instead)
* **Open Bar When Pointer Rests at the Top Edge** (on by default)
* **Keep in Dock** (Mac, on by default)
* **Set Up Another Computer** (also the **i** at the end of the bar), **Open Received Files**,
  **Link Key**, **Unlink This Computer**, **Uninstall** (Windows), **Quit**

## Building from source

Needs the Xcode command line tools and the .NET SDK (any recent version; it builds the
.NET Framework 4.8 exe on Mac or Windows).

```sh
python3 tools/make-install-guide.py                      # INSTALL.md from install/*.txt
mac/build.sh                                             # dist/DropDock.app and dist/DropDock-mac.zip
cd windows && dotnet build -c Release -o ../build/windows # build/windows/DropDock.exe
```

`PROTOCOL.md` describes the network protocol both apps share. `tests/interop` runs the Windows
networking code on any OS so it can be tested against the Mac app:

```sh
cd tests/interop && dotnet build -c Release -o ../../build/interop
../../build/interop/Interop selftest
```

The Mac app also has a command line mode for scripting and tests: `DropDock --cli info`,
`send <host> <port> <files>`, `clip <host> <port> <general|direct> <text>`, `link <host> <port>`.
