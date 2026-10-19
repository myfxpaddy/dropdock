import AppKit

/// The small icon in the menu bar. Everything in the bar is also reachable here with the keyboard.
@MainActor
final class StatusMenu: NSObject, NSMenuDelegate {
    static let shared = StatusMenu()

    private var item: NSStatusItem?
    private let menu = NSMenu()

    func start() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = item.button {
            let image = NSImage(systemSymbolName: "rectangle.topthird.inset.filled", accessibilityDescription: "DropDock")
            image?.isTemplate = true
            button.image = image
            button.imagePosition = .imageLeading
            button.setAccessibilityLabel("DropDock")
        }
        menu.delegate = self
        menu.autoenablesItems = false
        item.menu = menu
        self.item = item
        Hub.shared.observe { [weak self] in self?.refreshButton() }
    }

    private func refreshButton() {
        guard let button = item?.button else { return }
        if let p = Hub.shared.activeProgress {
            button.title = " \(Int(p * 100))%"
        } else if !button.title.isEmpty {
            button.title = ""
        }
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let hub = Hub.shared
        let me = Settings.shared.identity

        add("This computer: \(me.name)", enabled: false)
        add("Show DropDock Bar", key: "d") { BarController.shared.reveal(reason: .pinned) }
        add("Paste Clipboard to...", key: "v") { BarController.shared.startPaste() }
        add("Set Up Another Computer...") { SetupWindow.shared.show() }
        menu.addItem(.separator())

        add("Your computers", enabled: false)
        let linked = hub.linkedPeers
        if linked.isEmpty { add("    None linked yet", enabled: false) }
        for p in linked {
            let sub = NSMenu()
            let id = p.id
            sub.addItem(item("Send Files...") { BarController.shared.reveal(reason: .pinned); Self.chooseFiles(peerId: id) })
            sub.addItem(item("Paste Clipboard Here") { if let p = Hub.shared.peers[id] { Hub.shared.pasteClipboard(to: p) } })
            if hub.directClips[id] != nil {
                sub.addItem(item("Copy Their Last Clip") { Hub.shared.copyDirect(from: id) })
            }
            let entry = NSMenuItem(title: "    \(p.name)", action: nil, keyEquivalent: "")
            entry.image = NSImage(systemSymbolName: p.symbol, accessibilityDescription: nil)
            entry.submenu = sub
            menu.addItem(entry)
        }
        let nearby = hub.nearbyPeers
        if !nearby.isEmpty {
            add("Nearby, not linked", enabled: false)
            for p in nearby {
                let id = p.id
                add("    Link with \(p.name)...") { if let p = Hub.shared.peers[id] { Hub.shared.requestLink(p) } }
            }
        }
        menu.addItem(.separator())

        add("General", enabled: false)
        if let g = hub.general {
            add("    Copy \"\(String(g.preview.prefix(40)))\"") { Hub.shared.copyGeneral() }
        } else {
            add("    Nothing shared yet", enabled: false)
        }
        add("    Paste Clipboard to General") { Hub.shared.pasteClipboard(to: nil) }
        menu.addItem(.separator())

        add("Open Received Files") {
            let folder = Settings.shared.receiveFolder
            try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            NSWorkspace.shared.open(folder)
        }
        let keys = NSMenu()
        keys.addItem(item("Copy Link Key") {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(DDCrypto.display(code: Settings.shared.groupCode), forType: .string)
            Toast.shared.show(symbol: "key", title: "Link key copied",
                              body: "Enter it on another computer with Link Key > Enter a Link Key.")
        })
        keys.addItem(item("Enter a Link Key...") { Self.enterKey() })
        keys.addItem(.separator())
        keys.addItem(item("Unlink This Computer...") { Self.unlink() })
        let keysItem = NSMenuItem(title: "Link Key", action: nil, keyEquivalent: "")
        keysItem.submenu = keys
        menu.addItem(keysItem)
        menu.addItem(.separator())

        toggle("Start at Login", Settings.shared.startAtLogin) {
            Settings.shared.startAtLogin.toggle()
            if Settings.shared.startAtLogin { LoginItem.install() } else { LoginItem.remove() }
        }
        toggle("Put Clips Sent Here on the Clipboard", Settings.shared.autoCopyDirect) {
            Settings.shared.autoCopyDirect.toggle()
        }
        toggle("Open Bar When Pointer Rests at the Top Edge", Settings.shared.hoverReveal) {
            Settings.shared.hoverReveal.toggle()
        }
        let docked = Shortcuts.inDock
        toggle("Keep in Dock", docked) { Shortcuts.setInDock(!docked) }
        if !FileManager.default.fileExists(atPath: Shortcuts.desktopAlias.path) {
            add("Put an Alias on the Desktop") { Shortcuts.addDesktopAlias() }
        }
        menu.addItem(.separator())
        add("Quit DropDock", key: "q") { NSApp.terminate(nil) }
    }

    // MARK: Helpers

    private func item(_ title: String, key: String = "", _ action: @escaping () -> Void) -> NSMenuItem {
        let it = ClosureMenuItem(title: title, action: #selector(ClosureMenuItem.fire), keyEquivalent: key)
        it.target = it
        it.handler = action
        return it
    }

    private func add(_ title: String, key: String = "", enabled: Bool = true, _ action: (() -> Void)? = nil) {
        if let action {
            menu.addItem(item(title, key: key, action))
        } else {
            let it = NSMenuItem(title: title, action: nil, keyEquivalent: "")
            it.isEnabled = enabled
            menu.addItem(it)
        }
    }

    private func toggle(_ title: String, _ on: Bool, _ action: @escaping () -> Void) {
        let it = item(title, action)
        it.state = on ? .on : .off
        menu.addItem(it)
    }

    static func chooseFiles(peerId: String) {
        NSApp.activate(ignoringOtherApps: true)
        let open = NSOpenPanel()
        open.canChooseFiles = true
        open.canChooseDirectories = true
        open.allowsMultipleSelection = true
        open.prompt = "Send"
        BarController.shared.hide()
        open.begin { response in
            guard response == .OK else { return }
            let urls = open.urls
            Task { @MainActor in
                if let p = Hub.shared.peers[peerId] { Hub.shared.send(files: urls, to: p) }
            }
        }
    }

    static func enterKey() {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "Enter a Link Key"
        alert.informativeText = "Copy the key from another computer's DropDock menu (Link Key > Copy Link Key). This computer will join that computer's group."
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 260, height: 24))
        field.placeholderString = "XXXXX-XXXXX-XXXXX-XXXXX"
        field.setAccessibilityLabel("Link key")
        alert.accessoryView = field
        alert.addButton(withTitle: "Link")
        alert.addButton(withTitle: "Cancel")
        alert.window.initialFirstResponder = field
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        guard let code = DDCrypto.normalize(code: field.stringValue) else {
            let bad = NSAlert()
            bad.messageText = "That key is not valid"
            bad.informativeText = "A link key has 20 letters and numbers, like ABCDE-12345-FGHJK-67890. Check it and try again."
            bad.runModal()
            return
        }
        Hub.shared.adopt(code: code, from: "your other computers", peerId: nil)
    }

    static func unlink() {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "Unlink this computer?"
        alert.informativeText = "This computer gets a new link key. Your other computers will stop seeing it until you link it again."
        alert.addButton(withTitle: "Unlink")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        Hub.shared.newGroup()
    }
}

final class ClosureMenuItem: NSMenuItem {
    var handler: (() -> Void)?
    @objc func fire() { handler?() }
}
