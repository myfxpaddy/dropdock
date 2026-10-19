import CryptoKit
import Foundation
import Network

/// What a list of dropped files and folders turns into on the wire.
struct Manifest {
    var items: [[String: Any]] = []
    var files: [(url: URL, size: Int64)] = []
    var total: Int64 = 0

    var summary: String {
        let tops = items.compactMap { ($0["path"] as? String) }.filter { !$0.contains("/") }
        if tops.count == 1 { return tops[0] }
        return "\(tops.count) items"
    }

    static func build(_ urls: [URL]) throws -> Manifest {
        var m = Manifest()
        let fm = FileManager.default
        for url in urls {
            let base = url.lastPathComponent
            var isDir: ObjCBool = false
            guard fm.fileExists(atPath: url.path, isDirectory: &isDir) else { continue }
            if isDir.boolValue {
                m.items.append(["path": base, "size": 0, "dir": true])
                guard let en = fm.enumerator(atPath: url.path) else { continue }
                while let rel = en.nextObject() as? String {
                    let attrs = en.fileAttributes
                    let type = attrs?[.type] as? FileAttributeType
                    let name = (rel as NSString).lastPathComponent
                    if name == ".DS_Store" { continue }
                    let path = base + "/" + rel
                    if type == .typeDirectory {
                        m.items.append(["path": path, "size": 0, "dir": true])
                    } else if type == .typeRegular {
                        let size = (attrs?[.size] as? NSNumber)?.int64Value ?? 0
                        m.items.append(["path": path, "size": size, "dir": false])
                        m.files.append((url.appendingPathComponent(rel), size))
                        m.total += size
                    }
                }
            } else {
                let size = ((try? fm.attributesOfItem(atPath: url.path))?[.size] as? NSNumber)?.int64Value ?? 0
                m.items.append(["path": base, "size": size, "dir": false])
                m.files.append((url, size))
                m.total += size
            }
        }
        return m
    }
}

enum Client {
    static let chunk = 512 * 1024

    /// Connects and proves both sides hold the same group key. Returns an encrypted channel.
    static func session(host: String, port: UInt16) async throws -> SecureChannel {
        let me = Settings.shared.identity
        let wire = Wire(host: host, port: port)
        do {
            try await wire.open(timeout: 6)
            let cn = DDCrypto.random(32)
            try await wire.writeJSON(["t": "hello", "app": "dropdock", "v": 1, "id": me.id, "name": me.name,
                                      "os": me.os, "kind": me.kind, "tag": me.tag,
                                      "nonce": cn.base64EncodedString()])
            let reply = try await wire.readJSON()
            if reply["t"] as? String == "error" {
                throw reply["code"] as? String == "not-linked" ? WireError.notLinked : WireError.auth
            }
            guard reply["t"] as? String == "hello", let sn = b64(reply["nonce"]), sn.count == 32,
                  let proof = b64(reply["proof"]),
                  DDCrypto.hmacValid(proof, key: me.key, Data("dropdock/server".utf8), cn, sn)
            else { throw WireError.auth }
            let mine = DDCrypto.hmac(key: me.key, Data("dropdock/client".utf8), cn, sn)
            try await wire.writeJSON(["t": "auth", "proof": mine.base64EncodedString()])
            let okm = DDCrypto.hkdf(ikm: me.key, salt: cn + sn, info: "dropdock/session/v1", count: 128)
            let channel = SecureChannel(wire: wire,
                                        sealer: Sealer(enc: Data(okm[0..<32]), mac: Data(okm[32..<64])),
                                        opener: Opener(enc: Data(okm[64..<96]), mac: Data(okm[96..<128])))
            let ready = try await channel.receiveJSON(timeout: 15)
            guard ready["t"] as? String == "ready" else { throw WireError.bad("no ready") }
            return channel
        } catch let e as CryptoError {
            wire.close()
            _ = e
            throw WireError.auth
        } catch {
            wire.close()
            throw error
        }
    }

    static func sendFiles(_ m: Manifest, host: String, port: UInt16,
                          progress: @escaping (Double) -> Void) async throws -> [String] {
        let ch = try await session(host: host, port: port)
        defer { ch.close() }
        try await ch.send(json: ["t": "files", "items": m.items, "total": m.total, "count": m.files.count])
        let answer = try await ch.receiveJSON(timeout: 120)
        if answer["t"] as? String == "reject" {
            throw WireError.remote(answer["reason"] as? String ?? "The other computer refused the files")
        }
        guard answer["t"] as? String == "accept" else { throw WireError.bad("no accept") }

        var sent: Int64 = 0
        var lastReport = Date.distantPast
        for file in m.files {
            let handle = try FileHandle(forReadingFrom: file.url)
            defer { try? handle.close() }
            var remaining = file.size
            while remaining > 0 {
                let want = Int(min(remaining, Int64(chunk)))
                guard let data = try handle.read(upToCount: want), !data.isEmpty else {
                    throw WireError.remote("\(file.url.lastPathComponent) changed while sending")
                }
                try await ch.send(data: data)
                remaining -= Int64(data.count)
                sent += Int64(data.count)
                if Date().timeIntervalSince(lastReport) > 0.1 {
                    lastReport = Date()
                    progress(m.total > 0 ? Double(sent) / Double(m.total) : 1)
                }
            }
        }
        progress(1)
        try await ch.send(json: ["t": "end"])
        let done = try await ch.receiveJSON(timeout: 600)
        guard done["t"] as? String == "done" else { throw WireError.bad("no done") }
        return done["saved"] as? [String] ?? []
    }

    static func sendClip(_ clip: Clip, host: String, port: UInt16) async throws {
        let ch = try await session(host: host, port: port)
        defer { ch.close() }
        try await ch.send(json: ["t": "clip", "clip": clip.json])
        let answer = try await ch.receiveJSON(timeout: 30)
        guard answer["t"] as? String == "ok" else { throw WireError.bad("no ok") }
    }

    /// Asks another computer to share its group code. `onCode` gets the check code to show.
    static func requestLink(host: String, port: UInt16, onCode: @escaping (String) -> Void) async throws -> String {
        let me = Settings.shared.identity
        let wire = Wire(host: host, port: port)
        defer { wire.close() }
        try await wire.open(timeout: 6)
        let key = P256.KeyAgreement.PrivateKey()
        try await wire.writeJSON(["t": "link", "app": "dropdock", "v": 1, "id": me.id, "name": me.name,
                                  "os": me.os, "kind": me.kind,
                                  "pub": key.publicKey.x963Representation.base64EncodedString()])
        let wait = try await wire.readJSON()
        guard wait["t"] as? String == "link-wait", let theirs = b64(wait["pub"]) else { throw WireError.bad("no link-wait") }
        let secret = try DDCrypto.linkSecret(mine: key, theirs: theirs)
        onCode(DDCrypto.sas(secret))
        let result = try await wire.readJSON(timeout: 120)
        if result["t"] as? String == "link-deny" { throw WireError.denied }
        guard result["t"] as? String == "link-ok", let box = b64(result["box"]) else { throw WireError.bad("no link-ok") }
        let keys = DDCrypto.linkKeys(secret: secret)
        let plain = try Opener(enc: keys.enc, mac: keys.mac).open(box)
        guard plain.first == 1, let obj = try? JSON.decode(Data(plain.dropFirst())),
              let raw = obj["code"] as? String, let code = DDCrypto.normalize(code: raw)
        else { throw WireError.bad("bad link box") }
        return code
    }
}

/// Accepts connections from other computers.
final class Server: @unchecked Sendable {
    private var listener: NWListener?
    private let queue = DispatchQueue(label: "dropdock.server")
    private let lock = NSLock()
    private var active = 0
    private(set) var port: UInt16 = 0

    func start(preferred: UInt16, ready: @escaping (UInt16) -> Void) {
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        let port: NWEndpoint.Port = preferred == 0 ? .any : NWEndpoint.Port(rawValue: preferred)!
        guard let l = try? NWListener(using: params, on: port) else {
            if preferred != 0 { start(preferred: 0, ready: ready) }
            return
        }
        l.stateUpdateHandler = { [weak self, weak l] state in
            switch state {
            case .ready:
                let p = l?.port?.rawValue ?? preferred
                self?.port = p
                Log.info("server: listening on \(p)")
                ready(p)
            case .failed(let e):
                Log.info("server: \(e)")
                l?.cancel()
                if preferred != 0 { self?.start(preferred: 0, ready: ready) }
            default:
                break
            }
        }
        l.newConnectionHandler = { [weak self] conn in self?.accept(conn) }
        l.start(queue: queue)
        listener = l
    }

    private func accept(_ conn: NWConnection) {
        guard Server.isLocal(conn.endpoint) else { conn.cancel(); return }
        lock.lock()
        let allowed = active < 8
        if allowed { active += 1 }
        lock.unlock()
        guard allowed else { conn.cancel(); return }
        Task.detached { [self] in
            let activity = ProcessInfo.processInfo.beginActivity(options: .userInitiated, reason: "Receiving")
            await self.handle(Wire(conn))
            ProcessInfo.processInfo.endActivity(activity)
            self.release()
        }
    }

    private func release() {
        lock.lock()
        active -= 1
        lock.unlock()
    }

    /// Only talk to machines on the local network.
    static func isLocal(_ endpoint: NWEndpoint) -> Bool {
        guard case .hostPort(let host, _) = endpoint else { return false }
        switch host {
        case .ipv4(let a):
            let b = [UInt8](a.rawValue)
            return b[0] == 10 || b[0] == 127 || (b[0] == 172 && (16...31).contains(b[1]))
                || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
        case .ipv6(let a):
            if let v4 = a.asIPv4 { return isLocal(.hostPort(host: .ipv4(v4), port: 1)) }
            let b = [UInt8](a.rawValue)
            return a.isLoopback || a.isLinkLocal || (b[0] & 0xfe) == 0xfc
        default:
            return false
        }
    }

    private func handle(_ wire: Wire) async {
        defer { wire.close() }
        do {
            try await wire.open(timeout: 10)
            let first = try await wire.readJSON()
            guard first["app"] as? String == "dropdock" else { throw WireError.bad("not dropdock") }
            switch first["t"] as? String {
            case "hello": try await session(wire, first)
            case "link": try await link(wire, first)
            default: throw WireError.bad("unknown request")
            }
        } catch {
            Log.info("incoming from \(wire.remoteHost ?? "?"): \(error.localizedDescription)")
        }
    }

    private func session(_ wire: Wire, _ hello: [String: Any]) async throws {
        let me = Settings.shared.identity
        guard hello["tag"] as? String == me.tag else {
            try await wire.writeJSON(["t": "error", "code": "not-linked"])
            return
        }
        guard let cn = b64(hello["nonce"]), cn.count == 32 else { throw WireError.bad("nonce") }
        let sn = DDCrypto.random(32)
        let proof = DDCrypto.hmac(key: me.key, Data("dropdock/server".utf8), cn, sn)
        try await wire.writeJSON(["t": "hello", "app": "dropdock", "v": 1, "id": me.id, "name": me.name,
                                  "os": me.os, "kind": me.kind, "nonce": sn.base64EncodedString(),
                                  "proof": proof.base64EncodedString()])
        let auth = try await wire.readJSON()
        guard auth["t"] as? String == "auth", let theirs = b64(auth["proof"]),
              DDCrypto.hmacValid(theirs, key: me.key, Data("dropdock/client".utf8), cn, sn)
        else {
            try await wire.writeJSON(["t": "error", "code": "auth"])
            return
        }
        let okm = DDCrypto.hkdf(ikm: me.key, salt: cn + sn, info: "dropdock/session/v1", count: 128)
        let ch = SecureChannel(wire: wire,
                               sealer: Sealer(enc: Data(okm[64..<96]), mac: Data(okm[96..<128])),
                               opener: Opener(enc: Data(okm[0..<32]), mac: Data(okm[32..<64])))
        try await ch.send(json: ["t": "ready"])

        let peerId = hello["id"] as? String ?? ""
        let peerName = hello["name"] as? String ?? "Another computer"
        let request = try await ch.receiveJSON(timeout: 30)
        switch request["t"] as? String {
        case "files":
            do {
                try await Receiver(channel: ch, peerId: peerId, peerName: peerName).run(request)
            } catch {
                await MainActor.run { Hub.shared.setStatus(peerId, .failed("Receive failed")) }
                throw error
            }
        case "clip":
            guard var clip = Clip(json: request["clip"] as? [String: Any]) else { throw WireError.bad("empty clip") }
            clip.from = peerId
            if clip.fromName.isEmpty { clip.fromName = peerName }
            let received = clip
            await MainActor.run { Hub.shared.received(clip: received) }
            try await ch.send(json: ["t": "ok"])
        default:
            throw WireError.bad("unknown request")
        }
    }

    private func link(_ wire: Wire, _ request: [String: Any]) async throws {
        let me = Settings.shared.identity
        guard let theirs = b64(request["pub"]) else { throw WireError.bad("pub") }
        let key = P256.KeyAgreement.PrivateKey()
        let secret = try DDCrypto.linkSecret(mine: key, theirs: theirs)
        try await wire.writeJSON(["t": "link-wait", "app": "dropdock", "v": 1, "id": me.id, "name": me.name,
                                  "os": me.os, "kind": me.kind,
                                  "pub": key.publicKey.x963Representation.base64EncodedString()])
        let name = request["name"] as? String ?? "A computer"
        let code = DDCrypto.sas(secret)
        let allowed = await Hub.shared.confirmLink(name: name, code: code)
        guard allowed else {
            try await wire.writeJSON(["t": "link-deny"])
            return
        }
        let keys = DDCrypto.linkKeys(secret: secret)
        var plain = Data([1])
        plain.append(try JSON.encode(["code": Settings.shared.groupCode]))
        let box = try Sealer(enc: keys.enc, mac: keys.mac).seal(plain)
        try await wire.writeJSON(["t": "link-ok", "box": box.base64EncodedString()])
        Log.info("linked with \(name)")
        await MainActor.run { Hub.shared.linkGranted(to: name) }
    }
}

/// Receives one batch of files into a hidden staging folder, then moves it into place.
struct Receiver {
    let channel: SecureChannel
    let peerId: String
    let peerName: String

    private struct Item {
        let parts: [String]
        let size: Int64
        let dir: Bool
    }

    static func clean(_ path: String) -> [String]? {
        let parts = path.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        guard !parts.isEmpty, parts.count <= 64 else { return nil }
        for p in parts where p.isEmpty || p == "." || p == ".." || p.contains("\0") || p.utf8.count > 255 {
            return nil
        }
        return parts
    }

    static func unique(_ name: String, in folder: URL) -> URL {
        let fm = FileManager.default
        var candidate = folder.appendingPathComponent(name)
        guard fm.fileExists(atPath: candidate.path) else { return candidate }
        let ext = (name as NSString).pathExtension
        let stem = ext.isEmpty ? name : (name as NSString).deletingPathExtension
        var n = 2
        repeat {
            let next = ext.isEmpty ? "\(stem) (\(n))" : "\(stem) (\(n)).\(ext)"
            candidate = folder.appendingPathComponent(next)
            n += 1
        } while fm.fileExists(atPath: candidate.path)
        return candidate
    }

    func run(_ request: [String: Any]) async throws {
        guard let raw = request["items"] as? [[String: Any]], !raw.isEmpty, raw.count <= 200_000 else {
            throw WireError.bad("no items")
        }
        var items: [Item] = []
        for r in raw {
            guard let path = r["path"] as? String, let parts = Receiver.clean(path) else {
                try await channel.send(json: ["t": "reject", "reason": "A file name was not allowed"])
                return
            }
            let size = (r["size"] as? NSNumber)?.int64Value ?? 0
            guard size >= 0 else { throw WireError.bad("size") }
            items.append(Item(parts: parts, size: size, dir: r["dir"] as? Bool ?? false))
        }
        let total = items.filter { !$0.dir }.reduce(Int64(0)) { $0 + $1.size }

        let fm = FileManager.default
        let root = Settings.shared.receiveFolder
        try fm.createDirectory(at: root, withIntermediateDirectories: true)
        if let free = try? root.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
            .volumeAvailableCapacityForImportantUsage, free < total + 64 << 20 {
            let need = ByteCountFormatter.string(fromByteCount: total, countStyle: .file)
            try await channel.send(json: ["t": "reject", "reason": "Not enough free space on \(Settings.shared.identity.name) for \(need)"])
            return
        }

        let staging = root.appendingPathComponent(".dropdock-incoming-\(UUID().uuidString.prefix(8))", isDirectory: true)
        try fm.createDirectory(at: staging, withIntermediateDirectories: true)
        var finished = false
        defer { if !finished { try? fm.removeItem(at: staging) } }

        try await channel.send(json: ["t": "accept"])
        let peer = peerId
        await MainActor.run { Hub.shared.setStatus(peer, .receiving(0)) }

        var got: Int64 = 0
        var lastReport = Date.distantPast
        for item in items {
            let url = item.parts.reduce(staging) { $0.appendingPathComponent($1) }
            if item.dir {
                try fm.createDirectory(at: url, withIntermediateDirectories: true)
                continue
            }
            try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            guard fm.createFile(atPath: url.path, contents: nil) else { throw WireError.remote("Could not create \(item.parts.last!)") }
            let handle = try FileHandle(forWritingTo: url)
            defer { try? handle.close() }
            var remaining = item.size
            while remaining > 0 {
                guard case .data(let chunk) = try await channel.receive(timeout: 60),
                      Int64(chunk.count) <= remaining else { throw WireError.bad("file data") }
                try handle.write(contentsOf: chunk)
                remaining -= Int64(chunk.count)
                got += Int64(chunk.count)
                if Date().timeIntervalSince(lastReport) > 0.15 {
                    lastReport = Date()
                    let fraction = total > 0 ? Double(got) / Double(total) : 1
                    await MainActor.run { Hub.shared.setStatus(peer, .receiving(fraction)) }
                }
            }
        }
        let end = try await channel.receiveJSON(timeout: 60)
        guard end["t"] as? String == "end" else { throw WireError.bad("no end") }

        var tops: [String] = []
        for item in items where !tops.contains(item.parts[0]) { tops.append(item.parts[0]) }
        var saved: [URL] = []
        for name in tops {
            let dest = Receiver.unique(name, in: root)
            try fm.moveItem(at: staging.appendingPathComponent(name), to: dest)
            saved.append(dest)
        }
        try? fm.removeItem(at: staging)
        finished = true
        try await channel.send(json: ["t": "done", "saved": saved.map(\.lastPathComponent)])
        let name = peerName
        let urls = saved
        await MainActor.run { Hub.shared.received(files: urls, fromId: peer, fromName: name) }
    }
}
