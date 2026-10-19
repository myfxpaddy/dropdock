import AppKit

/// Starts DropDock at login through a per-user LaunchAgent, which also restarts it if it ever crashes.
enum LoginItem {
    static let label = "app.dropdock.agent"

    static var plist: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }

    static var launchedByLaunchd: Bool { Paths.env["XPC_SERVICE_NAME"] == label }
    static var domain: String { "gui/\(getuid())" }

    @discardableResult
    static func launchctl(_ args: [String]) -> Int32 {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        p.arguments = args
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        do {
            try p.run()
            p.waitUntilExit()
            return p.terminationStatus
        } catch {
            return -1
        }
    }

    /// Writes the LaunchAgent. Returns true when the file changed.
    @discardableResult
    static func install() -> Bool {
        guard let exe = Bundle.main.executablePath else { return false }
        let dict: [String: Any] = [
            "Label": label,
            "ProgramArguments": [exe],
            "RunAtLoad": true,
            "KeepAlive": ["SuccessfulExit": false],
            "ProcessType": "Interactive",
            "LimitLoadToSessionType": "Aqua",
            "ThrottleInterval": 10,
            "AssociatedBundleIdentifiers": [Bundle.main.bundleIdentifier ?? "app.dropdock.DropDock"],
        ]
        guard let data = try? PropertyListSerialization.data(fromPropertyList: dict, format: .xml, options: 0) else { return false }
        if (try? Data(contentsOf: plist)) == data { return false }
        try? FileManager.default.createDirectory(at: plist.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: plist, options: .atomic)
        return true
    }

    /// Takes effect at the next login. The running copy keeps going until you quit it.
    static func remove() { try? FileManager.default.removeItem(at: plist) }

    static var isLoaded: Bool { launchctl(["print", "\(domain)/\(label)"]) == 0 }

    /// Starts the launchd-managed copy. The caller quits afterwards.
    static func handOff(reload: Bool) -> Bool {
        if isLoaded {
            if !reload { return launchctl(["kickstart", "\(domain)/\(label)"]) == 0 }
            launchctl(["bootout", "\(domain)/\(label)"])
        }
        return launchctl(["bootstrap", domain, plist.path]) == 0
    }
}

/// Keeps DropDock in the Dock and on the Desktop. It is a menu bar app, so the Dock item is a launcher:
/// clicking it opens the bar, and dropping files on it asks which computer they go to.
enum Shortcuts {
    private static let dock = "com.apple.dock" as CFString
    private static let key = "persistent-apps" as CFString

    private static func dockItems() -> [[String: Any]] {
        CFPreferencesAppSynchronize(dock)
        return CFPreferencesCopyAppValue(key, dock) as? [[String: Any]] ?? []
    }

    private static func isOurs(_ item: [String: Any]) -> Bool {
        let tile = item["tile-data"] as? [String: Any]
        let file = tile?["file-data"] as? [String: Any]
        guard let s = file?["_CFURLString"] as? String, let url = URL(string: s) else { return false }
        return url.standardizedFileURL.path.hasSuffix("/DropDock.app")
    }

    static var inDock: Bool { dockItems().contains(where: isOurs) }

    static func setInDock(_ on: Bool) {
        var items = dockItems().filter { !isOurs($0) }
        if on {
            items.append(["tile-type": "file-tile",
                          "tile-data": ["file-data": ["_CFURLString": Bundle.main.bundleURL.absoluteString,
                                                      "_CFURLStringType": 15]]])
        }
        CFPreferencesSetAppValue(key, items as CFArray, dock)
        CFPreferencesAppSynchronize(dock)
        let restart = Process()
        restart.executableURL = URL(fileURLWithPath: "/usr/bin/killall")
        restart.arguments = ["Dock"]
        try? restart.run()
    }

    static var desktopAlias: URL {
        FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask)[0].appendingPathComponent("DropDock")
    }

    /// A real Finder alias, so it keeps working if the app moves.
    static func addDesktopAlias() {
        guard !FileManager.default.fileExists(atPath: desktopAlias.path) else { return }
        do {
            let data = try Bundle.main.bundleURL.bookmarkData(options: .suitableForBookmarkFile,
                                                              includingResourceValuesForKeys: nil, relativeTo: nil)
            try URL.writeBookmarkData(data, to: desktopAlias)
        } catch {
            Log.info("desktop alias: \(error.localizedDescription)")
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var pendingOpen: [URL] = []
    private var ready = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        if !Paths.isTest {
            if !becomeOnlyInstance() { exit(0) }
            if offerMoveToApplications() { return }
            if Settings.shared.startAtLogin {
                let changed = LoginItem.install()
                if !LoginItem.launchedByLaunchd && LoginItem.handOff(reload: changed) {
                    Log.info("handed off to launchd")
                    exit(0)
                }
            }
        }

        if !Paths.isTest && !Settings.shared.placedShortcuts && Bundle.main.bundlePath.hasSuffix(".app") {
            Settings.shared.placedShortcuts = true
            if !Shortcuts.inDock { Shortcuts.setInDock(true) }
            Shortcuts.addDesktopAlias()
        }

        Log.info("starting as \(Settings.shared.identity.name), tag \(Settings.shared.groupTag)")
        Hub.shared.start()
        StatusMenu.shared.start()
        EdgeWatcher.shared.start()
        _ = BarController.shared

        // Ask for Documents access now, not in the middle of the first transfer.
        let folder = Settings.shared.receiveFolder
        DispatchQueue.global(qos: .utility).async {
            try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        }

        DistributedNotificationCenter.default().addObserver(forName: Notification.Name("app.dropdock.show"),
                                                            object: nil, queue: .main) { _ in
            Task { @MainActor in BarController.shared.reveal(reason: .pinned) }
        }

        if !Settings.shared.welcomed {
            Settings.shared.welcomed = true
            Toast.shared.show(symbol: "rectangle.topthird.inset.filled", title: "DropDock is ready",
                              body: "Drag a file to the top edge of the screen, or rest the pointer there for a moment. Install DropDock on your other computers, then click Link.",
                              seconds: 14)
        }
        ready = true
        if Paths.isTest, let out = Paths.env["DROPDOCK_DEMO"] {
            Hub.shared.injectDemo()
            BarController.shared.reveal(reason: .pinned)
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) {
                BarController.shared.snapshot(to: URL(fileURLWithPath: out))
                SetupWindow.shared.snapshot(to: URL(fileURLWithPath: out).deletingLastPathComponent().appendingPathComponent("setup.png"))
            }
        }
        if !pendingOpen.isEmpty {
            BarController.shared.choose(files: pendingOpen)
            pendingOpen = []
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        Hub.shared.shutdown()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        BarController.shared.reveal(reason: .pinned)
        return false
    }

    /// Files dropped on the app icon (Dock, Desktop alias or Finder).
    func application(_ application: NSApplication, open urls: [URL]) {
        let files = urls.filter(\.isFileURL)
        if ready { BarController.shared.choose(files: files) } else { pendingOpen += files }
    }

    private func becomeOnlyInstance() -> Bool {
        let id = Bundle.main.bundleIdentifier ?? "app.dropdock.DropDock"
        func others() -> [NSRunningApplication] {
            NSRunningApplication.runningApplications(withBundleIdentifier: id)
                .filter { $0 != NSRunningApplication.current && !$0.isTerminated }
        }
        if others().isEmpty { return true }
        if LoginItem.launchedByLaunchd {
            // A copy started by hand may still be handing over. Give it a moment to quit.
            let deadline = Date().addingTimeInterval(4)
            while Date() < deadline && !others().isEmpty {
                RunLoop.current.run(until: Date().addingTimeInterval(0.1))
            }
            return others().isEmpty
        }
        DistributedNotificationCenter.default().postNotificationName(Notification.Name("app.dropdock.show"),
                                                                     object: nil, userInfo: nil, deliverImmediately: true)
        return false
    }

    /// Returns true when it moved the app and launched the new copy.
    private func offerMoveToApplications() -> Bool {
        let path = Bundle.main.bundlePath
        guard path.hasSuffix(".app"), !path.hasPrefix("/Applications/"),
              !path.hasPrefix(NSHomeDirectory() + "/Applications/"), !Settings.shared.askedToMove
        else { return false }
        Settings.shared.askedToMove = true
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "Move DropDock to Applications?"
        alert.informativeText = "DropDock starts with your Mac and keeps running in the background. Keeping it in Applications means it keeps working if you tidy up Downloads."
        alert.addButton(withTitle: "Move to Applications")
        alert.addButton(withTitle: "Not Now")
        guard alert.runModal() == .alertFirstButtonReturn else { return false }
        let fm = FileManager.default
        let dest = URL(fileURLWithPath: "/Applications/DropDock.app")
        do {
            if fm.fileExists(atPath: dest.path) { try fm.trashItem(at: dest, resultingItemURL: nil) }
            try fm.copyItem(at: Bundle.main.bundleURL, to: dest)
            try? fm.trashItem(at: Bundle.main.bundleURL, resultingItemURL: nil)
        } catch {
            let fail = NSAlert()
            fail.messageText = "Could not move DropDock"
            fail.informativeText = "\(error.localizedDescription)\n\nYou can drag DropDock into Applications yourself."
            fail.runModal()
            return false
        }
        let relaunch = Process()
        relaunch.executableURL = URL(fileURLWithPath: "/bin/sh")
        relaunch.arguments = ["-c", "sleep 1; /usr/bin/open \"\(dest.path)\""]
        try? relaunch.run()
        exit(0)
    }
}
