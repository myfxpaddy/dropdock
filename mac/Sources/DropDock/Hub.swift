import AppKit

enum PeerStatus: Equatable {
    case idle
    case sending(Double)
    case receiving(Double)
    case done(String)
    case failed(String)
    case linking(String)
}

@MainActor
final class Peer {
    let id: String
    var name: String
    var os: String
    var kind: String
    var address: String
    var port: UInt16
    var tag: String
    var generalTime: Int64 = 0
    var status: PeerStatus = .idle
    var pushedGeneral: Int64 = 0
    var expiry: DispatchWorkItem?
    var statusReset: DispatchWorkItem?

    init(_ b: Beacon) {
        id = b.id
        name = b.name
        os = b.os
        kind = b.kind
        address = b.address
        port = b.port
        tag = b.tag
    }

    var linked: Bool { tag == Settings.shared.groupTag }

    var symbol: String {
        if kind == "laptop" { return "laptopcomputer" }
        return os == "windows" ? "pc" : "desktopcomputer"
    }
}

/// Owns the peer list, the General clip and every send and receive. All UI reads from here.
@MainActor
final class Hub {
    static let shared = Hub()

    private(set) var peers: [String: Peer] = [:]
    private(set) var general: Clip?
    private(set) var directClips: [String: Clip] = [:]
    let discovery = Discovery()
    let server = Server()
    private var port: UInt16 = 0
    private var observers: [() -> Void] = []
    private var linkPromptOpen = false
    private var started = false

    func observe(_ f: @escaping () -> Void) { observers.append(f) }

    func changed() { observers.forEach { $0() } }

    var linkedPeers: [Peer] {
        peers.values.filter(\.linked).sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    var nearbyPeers: [Peer] {
        peers.values.filter { !$0.linked }.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    var activeProgress: Double? {
        let values = peers.values.compactMap { p -> Double? in
            switch p.status {
            case .sending(let f), .receiving(let f): return f
            default: return nil
            }
        }
        return values.isEmpty ? nil : values.reduce(0, +) / Double(values.count)
    }

    func start() {
        guard !started else { return }
        started = true
        general = loadGeneral()
        discovery.fields = { [weak self] in
            let me = Settings.shared.identity
            return ["id": me.id, "name": me.name, "os": me.os, "kind": me.kind,
                    "port": Int(self?.port ?? 0), "tag": me.tag, "gt": self?.general?.time ?? 0]
        }
        discovery.onBeacon = { [weak self] b in self?.beacon(b) }
        server.start(preferred: Settings.shared.preferredPort) { p in
            Task { @MainActor in
                Hub.shared.port = p
                Hub.shared.discovery.start()
            }
        }
        let ws = NSWorkspace.shared.notificationCenter
        ws.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { _ in
            Task { @MainActor in Hub.shared.discovery.goodbye() }
        }
        ws.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { _ in
            DispatchQueue.main.asyncAfter(deadline: .now() + 3) {
                Task { @MainActor in Hub.shared.discovery.announce() }
            }
        }
    }

    func shutdown() { discovery.goodbye() }

    /// Test sandbox only: fills the bar with sample computers so the layout can be checked.
    func injectDemo() {
        guard Paths.isTest else { return }
        let tag = Settings.shared.groupTag
        let samples: [(String, String, String, String, PeerStatus)] = [
            ("d1", "MacBook Air", "mac", "laptop", .idle),
            ("d2", "Office PC", "windows", "desktop", .sending(0.42)),
            ("d3", "Studio iMac", "mac", "desktop", .done("Sent")),
        ]
        for (id, name, os, kind, status) in samples {
            let p = Peer(Beacon(type: "here", id: id, name: name, os: os, kind: kind, port: 1, tag: tag, generalTime: 0, address: "127.0.0.1"))
            p.status = status
            peers[id] = p
        }
        let n = Peer(Beacon(type: "here", id: "d4", name: "Kids Laptop", os: "windows", kind: "laptop", port: 1, tag: "other", generalTime: 0, address: "127.0.0.1"))
        peers["d4"] = n
        var clip = Clip()
        clip.fromName = "Office PC"
        clip.text = "Quarterly numbers: revenue up 12%, see attached deck"
        general = clip
        var direct = Clip()
        direct.text = "Wi-Fi password for the studio"
        directClips["d1"] = direct
        changed()
    }

    // MARK: Peers

    private func beacon(_ b: Beacon) {
        if b.type == "bye" {
            if peers.removeValue(forKey: b.id) != nil { changed() }
            return
        }
        let peer: Peer
        var changedSomething = false
        if let existing = peers[b.id] {
            peer = existing
            if peer.name != b.name || peer.tag != b.tag || peer.address != b.address || peer.port != b.port {
                changedSomething = true
            }
            peer.name = b.name
            peer.os = b.os
            peer.kind = b.kind
            peer.address = b.address
            peer.port = b.port
            peer.tag = b.tag
        } else {
            peer = Peer(b)
            peers[b.id] = peer
            changedSomething = true
            Log.info("found \(b.name) at \(b.address):\(b.port) linked=\(peer.linked)")
        }
        peer.generalTime = b.generalTime
        peer.expiry?.cancel()
        let id = b.id
        let work = DispatchWorkItem {
            Task { @MainActor in
                if Hub.shared.peers.removeValue(forKey: id) != nil { Hub.shared.changed() }
            }
        }
        peer.expiry = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 125, execute: work)

        if peer.linked, let g = general, b.generalTime < g.time, peer.pushedGeneral != g.time {
            peer.pushedGeneral = g.time
            deliver(g, to: peer, quiet: true)
        }
        if changedSomething { changed() }
    }

    func setStatus(_ id: String, _ status: PeerStatus) {
        guard let peer = peers[id] else { return }
        peer.statusReset?.cancel()
        peer.status = status
        switch status {
        case .done, .failed:
            let work = DispatchWorkItem {
                Task { @MainActor in
                    guard let p = Hub.shared.peers[id] else { return }
                    p.status = .idle
                    Hub.shared.changed()
                }
            }
            peer.statusReset = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 4, execute: work)
        default:
            break
        }
        changed()
    }

    private func progress(_ id: String, _ fraction: Double) {
        guard let peer = peers[id], case .sending = peer.status else { return }
        setStatus(id, .sending(fraction))
    }

    // MARK: Files

    func send(files urls: [URL], to peer: Peer) {
        guard peer.linked, !urls.isEmpty else { return }
        if case .sending = peer.status {
            Toast.shared.show(symbol: "hourglass", title: "Still sending to \(peer.name)",
                              body: "Wait for the current transfer to finish.")
            return
        }
        let id = peer.id, host = peer.address, port = peer.port, name = peer.name
        setStatus(id, .sending(0))
        Task.detached {
            let activity = ProcessInfo.processInfo.beginActivity(options: .userInitiated, reason: "Sending files")
            defer { ProcessInfo.processInfo.endActivity(activity) }
            do {
                let m = try Manifest.build(urls)
                guard !m.items.isEmpty else { throw WireError.remote("Nothing to send") }
                _ = try await Client.sendFiles(m, host: host, port: port) { f in
                    Task { @MainActor in Hub.shared.progress(id, f) }
                }
                Log.info("sent \(m.summary) to \(name)")
                await MainActor.run {
                    Hub.shared.setStatus(id, .done("Sent"))
                    Toast.shared.show(symbol: "checkmark.circle", title: "Sent to \(name)",
                                      body: "\(m.summary) is in Documents/DropDock on \(name).")
                }
            } catch {
                Log.info("send to \(name) failed: \(error)")
                await MainActor.run {
                    Hub.shared.setStatus(id, .failed("Failed"))
                    Toast.shared.show(symbol: "exclamationmark.triangle", title: "Could not send to \(name)",
                                      body: Hub.explain(error, name: name))
                }
            }
        }
    }

    func sendToAll(files urls: [URL]) {
        let targets = linkedPeers
        guard !targets.isEmpty else {
            Toast.shared.show(symbol: "wifi.exclamationmark", title: "No linked computers",
                              body: "Link a computer first. Open DropDock on it and click Link.")
            return
        }
        targets.forEach { send(files: urls, to: $0) }
    }

    func received(files: [URL], fromId: String, fromName: String) {
        setStatus(fromId, .done("Received"))
        Log.info("received \(files.count) item(s) from \(fromName)")
        let what = files.count == 1 ? files[0].lastPathComponent : "\(files.count) items"
        Toast.shared.show(symbol: "tray.and.arrow.down", title: "From \(fromName)",
                          body: "\(what) saved to Documents/DropDock.", actionTitle: "Show") {
            NSWorkspace.shared.activateFileViewerSelecting(files)
        }
    }

    // MARK: Clips

    /// Sends the current clipboard to one computer, or to General when `peer` is nil.
    func pasteClipboard(to peer: Peer?) {
        guard let clip = Clip.read(from: .general) else {
            Toast.shared.show(symbol: "doc.on.clipboard", title: "Your clipboard is empty",
                              body: "Copy some text or an image first, then click Paste.")
            return
        }
        share(clip, to: peer)
    }

    func share(_ content: Clip, to peer: Peer?) {
        var clip = content
        let me = Settings.shared.identity
        clip.id = UUID().uuidString.lowercased()
        clip.from = me.id
        clip.fromName = me.name
        clip.time = Int64(Date().timeIntervalSince1970 * 1000)
        if let peer {
            clip.scope = "direct"
            deliver(clip, to: peer, quiet: false)
        } else {
            clip.scope = "general"
            setGeneral(clip)
            let targets = linkedPeers
            for p in targets {
                p.pushedGeneral = clip.time
                deliver(clip, to: p, quiet: true)
            }
            let note = targets.isEmpty ? "Computers will pick it up when they come online."
                : "Any of your computers can copy it from the bar."
            Toast.shared.show(symbol: "globe", title: "Shared to General", body: note)
        }
    }

    private func deliver(_ clip: Clip, to peer: Peer, quiet: Bool) {
        let id = peer.id, host = peer.address, port = peer.port, name = peer.name
        Task.detached {
            do {
                try await Client.sendClip(clip, host: host, port: port)
                if !quiet { await MainActor.run { Hub.shared.setStatus(id, .done("Pasted")) } }
            } catch {
                Log.info("clip to \(name) failed: \(error)")
                if !quiet {
                    await MainActor.run {
                        Hub.shared.setStatus(id, .failed("Failed"))
                        Toast.shared.show(symbol: "exclamationmark.triangle", title: "Could not paste to \(name)",
                                          body: Hub.explain(error, name: name))
                    }
                }
            }
        }
    }

    func received(clip: Clip) {
        if clip.scope == "general" {
            if let g = general, g.time >= clip.time { return }
            setGeneral(clip)
            if let p = peers[clip.from] { p.pushedGeneral = clip.time }
            Toast.shared.show(symbol: "globe", title: "General from \(clip.fromName)", body: clip.preview,
                              actionTitle: "Copy") { Hub.shared.copyGeneral() }
        } else {
            directClips[clip.from] = clip
            if Settings.shared.autoCopyDirect && !CLI.active {
                clip.write()
                Toast.shared.show(symbol: "doc.on.clipboard.fill", title: "From \(clip.fromName), ready to paste",
                                  body: clip.preview + "\nIt is on your clipboard. Press Command-V.")
            } else {
                Toast.shared.show(symbol: "doc.on.clipboard", title: "\(clip.fromName) sent you a clip",
                                  body: clip.preview, actionTitle: "Copy") { clip.write() }
            }
            changed()
        }
    }

    @discardableResult
    func copyGeneral() -> Bool {
        guard let g = general else { return false }
        g.write()
        return true
    }

    @discardableResult
    func copyDirect(from id: String) -> Bool {
        guard let c = directClips[id] else { return false }
        c.write()
        return true
    }

    private func setGeneral(_ clip: Clip) {
        general = clip
        if let data = try? JSON.encode(clip.json) {
            try? data.write(to: Paths.general, options: .atomic)
            chmod(Paths.general.path, 0o600)
        }
        changed()
    }

    private func loadGeneral() -> Clip? {
        guard let data = try? Data(contentsOf: Paths.general), let obj = try? JSON.decode(data) else { return nil }
        return Clip(json: obj)
    }

    // MARK: Linking

    func requestLink(_ peer: Peer) {
        guard !peer.linked else { return }
        if case .linking = peer.status { return }
        let id = peer.id, host = peer.address, port = peer.port, name = peer.name
        setStatus(id, .linking("Asking"))
        Task.detached {
            do {
                let code = try await Client.requestLink(host: host, port: port) { sas in
                    Task { @MainActor in
                        Hub.shared.setStatus(id, .linking(sas))
                        Toast.shared.show(symbol: "link", title: "Click Allow on \(name)",
                                          body: "Check that \(name) shows the code \(sas).", seconds: 30)
                    }
                }
                await MainActor.run { Hub.shared.adopt(code: code, from: name, peerId: id) }
            } catch {
                Log.info("link with \(name) failed: \(error)")
                await MainActor.run {
                    Hub.shared.setStatus(id, .failed(error as? WireError == .denied ? "Declined" : "Failed"))
                    Toast.shared.show(symbol: "link", title: "Not linked with \(name)",
                                      body: Hub.explain(error, name: name))
                }
            }
        }
    }

    func adopt(code: String, from name: String, peerId: String?) {
        Settings.shared.setGroupCode(code)
        peers.values.forEach { $0.pushedGeneral = 0 }
        if let peerId { setStatus(peerId, .done("Linked")) }
        discovery.announce()
        changed()
        Toast.shared.show(symbol: "link", title: "Linked with \(name)",
                          body: "Your computers can now send each other files and clips.")
    }

    func linkGranted(to name: String) {
        discovery.announce()
        Toast.shared.show(symbol: "link", title: "\(name) is linked",
                          body: "It can now send files and clips to this computer.")
    }

    func newGroup() {
        Settings.shared.setGroupCode(DDCrypto.newGroupCode())
        discovery.announce()
        changed()
    }

    /// Shown while another computer asks to link. Runs outside the main actor job so the app keeps working.
    func confirmLink(name: String, code: String) async -> Bool {
        if Paths.isTest && Paths.env["DROPDOCK_TEST_AUTOAPPROVE"] == "1" { return true }
        if CLI.active { return false }
        guard !linkPromptOpen else { return false }
        linkPromptOpen = true
        defer { linkPromptOpen = false }
        return await withCheckedContinuation { cont in
            DispatchQueue.main.async {
                NSApp.activate(ignoringOtherApps: true)
                let alert = NSAlert()
                alert.messageText = "Link \(name) with this computer?"
                alert.informativeText = "\(name) will be able to send files and clips to this computer, and this computer to it.\n\nOnly allow it if \(name) shows the code \(code)."
                alert.addButton(withTitle: "Allow")
                alert.addButton(withTitle: "Don't Allow")
                alert.window.level = .floating
                let timer = Timer(timeInterval: 90, repeats: false) { _ in NSApp.abortModal() }
                RunLoop.main.add(timer, forMode: .modalPanel)
                let response = alert.runModal()
                timer.invalidate()
                cont.resume(returning: response == .alertFirstButtonReturn)
            }
        }
    }

    nonisolated static func explain(_ error: Error, name: String) -> String {
        if let w = error as? WireError {
            switch w {
            case .notLinked: return "\(name) is not linked with this computer. Link it from the DropDock menu."
            case .auth: return "\(name) has a different link key. Link the two computers again."
            case .timeout, .closed: return "\(name) stopped responding. Check it is awake and on the same Wi-Fi."
            case .denied: return "\(name) did not allow the link."
            default: return w.localizedDescription
            }
        }
        let ns = error as NSError
        if ns.domain == NSPOSIXErrorDomain || ns.domain == "Network.NWError" {
            return "Could not reach \(name). Check it is awake, on the same Wi-Fi, and that DropDock is running there."
        }
        return error.localizedDescription
    }
}
