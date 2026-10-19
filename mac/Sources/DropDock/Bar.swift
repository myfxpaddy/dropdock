import AppKit

final class BarPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// The hidden bar at the top of the screen. It appears when a drag reaches the top edge, when the
/// pointer rests at the top center, or from the menu bar icon.
@MainActor
final class BarController: NSObject {
    static let shared = BarController()

    static let dragTypes: [NSPasteboard.PasteboardType] =
        [.fileURL, .string, .html, .rtf, .png, .tiff]
        + NSFilePromiseReceiver.readableDraggedTypes.map { NSPasteboard.PasteboardType($0) }

    enum Reason { case drag, hover, pinned }

    private let panel: BarPanel
    private let effect = NSVisualEffectView()
    private let separator = NSView()
    private var tiles: [String: TileView] = [:]
    private var order: [String] = []
    private(set) var isVisible = false
    private var reason: Reason = .hover
    private var timer: Timer?
    private var outsideSince: Date?
    private var everInside = false
    private var holdUntil = Date.distantPast
    private var screen: NSScreen?
    private var anchorX: CGFloat = 0
    private var pickMode = false
    private var pendingFiles: [URL]?
    private let promiseQueue = OperationQueue()

    override init() {
        panel = BarPanel(contentRect: NSRect(x: 0, y: 0, width: 400, height: 104),
                         styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        super.init()
        panel.isFloatingPanel = true
        panel.level = .popUpMenu
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.animationBehavior = .none
        panel.appearance = NSAppearance(named: .vibrantDark)
        panel.setAccessibilityLabel("DropDock bar")

        effect.material = .hudWindow
        effect.blendingMode = .behindWindow
        effect.state = .active
        effect.maskImage = BarController.roundedMask(radius: 18)
        panel.contentView = effect

        separator.wantsLayer = true
        separator.layer?.backgroundColor = NSColor(white: 1, alpha: 0.16).cgColor
        effect.addSubview(separator)

        promiseQueue.qualityOfService = .userInitiated
        Hub.shared.observe { [weak self] in self?.reload() }
    }

    static func roundedMask(radius: CGFloat) -> NSImage {
        let side = radius * 2 + 1
        let image = NSImage(size: NSSize(width: side, height: side), flipped: false) { rect in
            NSColor.black.setFill()
            NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius).fill()
            return true
        }
        image.capInsets = NSEdgeInsets(top: radius, left: radius, bottom: radius, right: radius)
        image.resizingMode = .stretch
        return image
    }

    // MARK: Showing and hiding

    func reveal(reason r: Reason, at point: NSPoint? = nil) {
        if isVisible {
            if r == .pinned || r == .drag { reason = r }
            outsideSince = nil
            return
        }
        let p = point ?? NSEvent.mouseLocation
        let target = NSScreen.screens.first { NSMouseInRect(p, $0.frame, false) } ?? NSScreen.main ?? NSScreen.screens[0]
        screen = target
        anchorX = p.x
        reason = r
        everInside = false
        outsideSince = nil
        if r != .pinned { pickMode = false }
        apply(models())
        let final = frame(for: currentSize, on: target)
        let still = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        panel.setFrame(still ? final : final.offsetBy(dx: 0, dy: 12), display: false)
        panel.alphaValue = 0
        panel.orderFrontRegardless()
        isVisible = true
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = still ? 0.1 : 0.18
            ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
            panel.animator().setFrame(final, display: true)
            panel.animator().alphaValue = 1
        }
        startTimer()
    }

    func hide() {
        guard isVisible else { return }
        isVisible = false
        timer?.invalidate()
        timer = nil
        pickMode = false
        pendingFiles = nil
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.12
            panel.animator().alphaValue = 0
        }, completionHandler: {
            Task { @MainActor in
                let bar = BarController.shared
                if !bar.isVisible { bar.panel.orderOut(nil) }
            }
        })
    }

    /// Files dropped on the app icon: show the bar and let the user pick where they go.
    func choose(files: [URL]) {
        guard !files.isEmpty else { return }
        hide()
        pendingFiles = files
        reveal(reason: .pinned)
        pendingFiles = files
        reload()
    }

    func startPaste() {
        reveal(reason: .pinned)
        pickMode = true
        reload()
    }

    func globalMouseDown(_ p: NSPoint) {
        if isVisible && reason != .drag && !keepRegion.contains(p) { hide() }
    }

    private var keepRegion: NSRect {
        let f = panel.frame
        let top = screen?.frame.maxY ?? f.maxY
        return NSRect(x: f.minX - 30, y: f.minY - 30, width: f.width + 60, height: top - f.minY + 30)
    }

    private func startTimer() {
        timer?.invalidate()
        let t = Timer(timeInterval: 0.15, repeats: true) { _ in
            Task { @MainActor in BarController.shared.tick() }
        }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    private func tick() {
        guard isVisible else { return }
        let p = NSEvent.mouseLocation
        if keepRegion.contains(p) {
            outsideSince = nil
            if panel.frame.contains(p) { everInside = true }
            return
        }
        if Date() < holdUntil { return }
        let now = Date()
        if outsideSince == nil { outsideSince = now }
        let away = now.timeIntervalSince(outsideSince!)
        var limit: TimeInterval
        switch reason {
        case .drag: limit = NSEvent.pressedMouseButtons & 1 != 0 ? 0.35 : 0.15
        case .hover: limit = 0.4
        case .pinned: limit = everInside ? 0.6 : 10
        }
        let linking = Hub.shared.peers.values.contains { if case .linking = $0.status { return true }; return false }
        if pickMode || pendingFiles != nil || linking { limit = max(limit, everInside ? 1.2 : 10) }
        if away > limit { hide() }
    }

    /// Centered under the pointer, kept fully on the screen.
    private func frame(for size: NSSize, on s: NSScreen) -> NSRect {
        let notch = s.safeAreaInsets.top
        let top = s.frame.maxY - (notch > 0 ? notch + 4 : 4)
        let x = min(max(anchorX - size.width / 2, s.frame.minX + 8), s.frame.maxX - size.width - 8)
        return NSRect(x: x.rounded(), y: top - size.height, width: size.width, height: size.height)
    }

    /// Writes the bar's window id and bounds next to `url`, for checking the layout in tests.
    func snapshot(to url: URL) {
        let f = panel.frame
        let info = "\(panel.windowNumber) \(Int(f.minX)) \(Int(f.minY)) \(Int(f.width)) \(Int(f.height))"
        try? info.write(to: url, atomically: true, encoding: .utf8)
        // Render the layer tree over a dark backdrop (the blur itself cannot be captured this way).
        let scale: CGFloat = 2
        let size = effect.bounds.size
        guard let ctx = CGContext(data: nil, width: Int(size.width * scale), height: Int(size.height * scale),
                                  bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue),
              let layer = effect.layer else { return }
        ctx.scaleBy(x: scale, y: scale)
        ctx.setFillColor(NSColor(white: 0.13, alpha: 1).cgColor)
        ctx.addPath(CGPath(roundedRect: CGRect(origin: .zero, size: size), cornerWidth: 18, cornerHeight: 18, transform: nil))
        ctx.fillPath()
        layer.render(in: ctx)
        if let image = ctx.makeImage() {
            let rep = NSBitmapImageRep(cgImage: image)
            try? rep.representation(using: .png, properties: [:])?.write(to: url.deletingPathExtension().appendingPathExtension("png"))
        }
    }

    // MARK: Content

    private var currentSize = NSSize(width: 400, height: 104)

    private func reload() {
        guard isVisible else { return }
        apply(models())
    }

    private func models() -> [TileModel] {
        let hub = Hub.shared
        let choosing = pickMode || pendingFiles != nil
        var list: [TileModel] = []

        if let files = pendingFiles {
            list.append(TileModel(key: "paste", symbol: "doc.on.doc", title: files.count == 1 ? files[0].lastPathComponent : "\(files.count) items",
                                  subtitle: "Click to cancel", width: 96, acceptsDrops: false,
                                  help: "Pick a computer to send to, or click here to cancel."))
        } else if pickMode {
            list.append(TileModel(key: "paste", symbol: "xmark.circle", title: "Cancel", subtitle: "Pick a target",
                                  width: 84, acceptsDrops: false, help: "Cancel pasting."))
        } else {
            list.append(TileModel(key: "paste", symbol: "doc.on.clipboard", title: "Paste", subtitle: "Clipboard",
                                  width: 84, acceptsDrops: false,
                                  help: "Send what you copied to one computer, or to General for all of them."))
        }

        var general = TileModel(key: "general", symbol: "globe", title: "General",
                                subtitle: "Paste here to share with every computer", width: 214, wide: true,
                                help: "General is shared by all your computers. Drop files here to send them to every computer.")
        if let g = hub.general {
            general.subtitle = "\(g.fromName): \(g.preview)"
            general.actionTitle = "Copy"
        }
        if choosing {
            general.highlight = true
            general.actionTitle = nil
            general.subtitle = pickMode ? "Paste to every computer" : "Send to every linked computer"
        }
        list.append(general)

        for p in hub.linkedPeers {
            var m = TileModel(key: p.id, symbol: p.symbol, title: p.name, subtitle: "Ready",
                              help: "\(p.name). Drop files here to send them. Click to choose files.")
            switch p.status {
            case .sending(let f):
                m.subtitle = "Sending \(Int(f * 100))%"
                m.progress = f
            case .receiving(let f):
                m.subtitle = "Receiving \(Int(f * 100))%"
                m.progress = f
            case .done(let s):
                m.subtitle = s
                m.subtitleColor = .systemGreen
            case .failed(let s):
                m.subtitle = s
                m.subtitleColor = NSColor(red: 1, green: 0.55, blue: 0.5, alpha: 1)
            case .linking(let c):
                m.subtitle = c
            case .idle:
                if hub.directClips[p.id] != nil { m.actionTitle = "Copy" }
            }
            if choosing {
                m.highlight = true
                m.actionTitle = nil
                if case .idle = p.status { m.subtitle = pickMode ? "Paste here" : "Send here" }
            }
            list.append(m)
        }

        for p in hub.nearbyPeers {
            var m = TileModel(key: p.id, symbol: p.symbol, title: p.name, subtitle: "Click to link",
                              dimmed: true, acceptsDrops: false,
                              help: "\(p.name) has DropDock but is not linked yet. Click to link it.")
            switch p.status {
            case .linking(let c):
                m.subtitle = c == "Asking" ? "Asking..." : "Code \(c)"
                m.dimmed = false
            case .failed(let s):
                m.subtitle = s
                m.subtitleColor = NSColor(red: 1, green: 0.55, blue: 0.5, alpha: 1)
            case .done(let s):
                m.subtitle = s
            default:
                break
            }
            list.append(m)
        }

        if hub.peers.isEmpty {
            list.append(TileModel(key: "empty", symbol: "wifi", title: "Looking for your computers",
                                  subtitle: "Install DropDock on your other computers on this Wi-Fi.",
                                  width: 236, wide: true, acceptsDrops: false,
                                  help: "No other computers found yet. Click to see how to set one up."))
        }
        if !choosing {
            list.append(TileModel(key: "setup", symbol: "info.circle", title: "", subtitle: "", width: 36,
                                  compact: true, acceptsDrops: false,
                                  help: "Set up DropDock on another computer"))
        }
        return list
    }

    private func apply(_ models: [TileModel]) {
        let keys = models.map(\.key)
        for (key, tile) in tiles where !keys.contains(key) {
            tile.removeFromSuperview()
            tiles[key] = nil
        }
        for m in models {
            if let tile = tiles[m.key] {
                tile.update(m)
            } else {
                let tile = TileView(m)
                connect(tile, key: m.key)
                tiles[m.key] = tile
                effect.addSubview(tile)
            }
        }
        order = keys
        layoutTiles()
    }

    private func layoutTiles() {
        let pad: CGFloat = 10, gap: CGFloat = 8
        var x = pad
        separator.isHidden = true
        for (i, key) in order.enumerated() {
            guard let tile = tiles[key] else { continue }
            tile.frame = NSRect(x: x, y: pad, width: tile.model.width, height: TileView.height)
            x += tile.model.width + gap
            if key == "general" && i < order.count - 1 {
                separator.isHidden = false
                separator.frame = NSRect(x: x + 2, y: pad + 14, width: 1, height: TileView.height - 28)
                x += 13
            }
        }
        currentSize = NSSize(width: x - gap + pad, height: TileView.height + pad * 2)
        if isVisible, let s = screen {
            panel.setFrame(frame(for: currentSize, on: s), display: true)
        }
    }

    private func connect(_ tile: TileView, key: String) {
        tile.onClick = { [weak self] in self?.clicked(key) }
        tile.onAction = { [weak self] in self?.copy(key) }
        tile.onDrop = { [weak self] info in self?.dropped(info, on: key) ?? false }
        tile.onDragEnter = { [weak self] in self?.everInside = true }
    }

    // MARK: Actions

    private func clicked(_ key: String) {
        let hub = Hub.shared
        switch key {
        case "paste":
            if pendingFiles != nil {
                hide()
                return
            }
            pickMode.toggle()
            reload()
        case "general":
            if let files = pendingFiles {
                pendingFiles = nil
                hub.sendToAll(files: files)
                holdUntil = Date().addingTimeInterval(1.2)
                reload()
            } else if pickMode {
                pickMode = false
                hub.pasteClipboard(to: nil)
                reload()
            } else {
                copy(key)
            }
        case "empty", "setup":
            SetupWindow.shared.show()
        default:
            guard let peer = hub.peers[key] else { return }
            if !peer.linked {
                hub.requestLink(peer)
            } else if let files = pendingFiles {
                pendingFiles = nil
                hub.send(files: files, to: peer)
                holdUntil = Date().addingTimeInterval(1.2)
                reload()
            } else if pickMode {
                pickMode = false
                hub.pasteClipboard(to: peer)
                reload()
            } else if hub.directClips[key] != nil {
                copy(key)
            } else {
                chooseFiles(for: peer)
            }
        }
    }

    private func copy(_ key: String) {
        let hub = Hub.shared
        if key == "general" {
            tiles[key]?.flash(hub.copyGeneral() ? "Copied. Press Command-V to paste." : "Nothing shared yet")
        } else {
            tiles[key]?.flash(hub.copyDirect(from: key) ? "Copied" : "Nothing to copy")
        }
    }

    private func chooseFiles(for peer: Peer) {
        hide()
        NSApp.activate(ignoringOtherApps: true)
        let open = NSOpenPanel()
        open.canChooseFiles = true
        open.canChooseDirectories = true
        open.allowsMultipleSelection = true
        open.prompt = "Send"
        open.message = "Choose files or folders to send to \(peer.name)"
        let id = peer.id
        open.begin { response in
            guard response == .OK else { return }
            let urls = open.urls
            Task { @MainActor in
                if let p = Hub.shared.peers[id] { Hub.shared.send(files: urls, to: p) }
            }
        }
    }

    private func dropped(_ info: NSDraggingInfo, on key: String) -> Bool {
        let hub = Hub.shared
        let target: Peer? = key == "general" ? nil : hub.peers[key]
        if key != "general" && target?.linked != true { return false }
        holdUntil = Date().addingTimeInterval(1.4)
        let pb = info.draggingPasteboard

        let send: ([URL]) -> Void = { urls in
            if let target { hub.send(files: urls, to: target) } else { hub.sendToAll(files: urls) }
        }

        if let urls = pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL],
           !urls.isEmpty {
            send(urls)
            return true
        }

        if let promises = pb.readObjects(forClasses: [NSFilePromiseReceiver.self], options: nil) as? [NSFilePromiseReceiver],
           !promises.isEmpty {
            let dir = FileManager.default.temporaryDirectory
                .appendingPathComponent("DropDock-\(UUID().uuidString.prefix(8))", isDirectory: true)
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let group = DispatchGroup()
            let lock = NSLock()
            var collected: [URL] = []
            for promise in promises {
                for _ in promise.fileNames { group.enter() }
                promise.receivePromisedFiles(atDestination: dir, options: [:], operationQueue: promiseQueue) { url, error in
                    if error == nil {
                        lock.lock(); collected.append(url); lock.unlock()
                    }
                    group.leave()
                }
            }
            group.notify(queue: .main) {
                let urls = collected
                Task { @MainActor in if !urls.isEmpty { send(urls) } }
            }
            return true
        }

        if let clip = Clip.read(from: pb) {
            hub.share(clip, to: target)
            return true
        }
        return false
    }
}
