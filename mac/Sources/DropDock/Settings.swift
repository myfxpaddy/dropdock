import Foundation
import IOKit.ps
import SystemConfiguration

/// Where DropDock keeps its files. DROPDOCK_HOME moves everything into one folder (used by tests).
enum Paths {
    static let env = ProcessInfo.processInfo.environment
    static let isTest = env["DROPDOCK_HOME"] != nil

    static let home: URL = {
        if let h = env["DROPDOCK_HOME"] { return URL(fileURLWithPath: h, isDirectory: true) }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("DropDock", isDirectory: true)
    }()

    static var state: URL { home.appendingPathComponent("state.json") }
    static var general: URL { home.appendingPathComponent("general.json") }
    static var log: URL { home.appendingPathComponent("dropdock.log") }

    static var defaultReceive: URL {
        if isTest { return home.appendingPathComponent("Received", isDirectory: true) }
        return FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("DropDock", isDirectory: true)
    }
}

/// A consistent copy of the identity and group, safe to use from any thread.
struct Identity {
    let id: String
    let name: String
    let os: String
    let kind: String
    let code: String
    let key: Data
    let tag: String
}

final class Settings: @unchecked Sendable {
    static let shared = Settings()

    private struct Stored: Codable {
        var deviceId: String
        var groupCode: String
        var startAtLogin: Bool?
        var autoCopyDirect: Bool?
        var hoverReveal: Bool?
        var receiveFolder: String?
        var askedToMove: Bool?
        var welcomed: Bool?
        var placedShortcuts: Bool?
    }

    private let lock = NSLock()
    private var stored: Stored
    private var key = Data()
    private var tag = ""

    let deviceName: String
    let kind: String
    let preferredPort: UInt16

    private init() {
        try? FileManager.default.createDirectory(at: Paths.home, withIntermediateDirectories: true)
        if let data = try? Data(contentsOf: Paths.state),
           let s = try? JSONDecoder().decode(Stored.self, from: data),
           DDCrypto.normalize(code: s.groupCode) != nil {
            stored = s
        } else {
            stored = Stored(deviceId: UUID().uuidString.lowercased(), groupCode: DDCrypto.newGroupCode())
        }
        deviceName = Paths.env["DROPDOCK_NAME"]
            ?? (SCDynamicStoreCopyComputerName(nil, nil) as String?)
            ?? ProcessInfo.processInfo.hostName
        kind = Settings.detectKind()
        preferredPort = UInt16(Paths.env["DROPDOCK_PORT"] ?? "") ?? 47821
        recomputeKeys()
        save()
    }

    private func recomputeKeys() {
        key = DDCrypto.groupKey(code: stored.groupCode)
        tag = DDCrypto.groupTag(key: key)
    }

    private func save() {
        guard let data = try? JSONEncoder().encode(stored) else { return }
        try? data.write(to: Paths.state, options: .atomic)
        chmod(Paths.state.path, 0o600)
    }

    private func locked<T>(_ f: () -> T) -> T {
        lock.lock(); defer { lock.unlock() }
        return f()
    }

    var identity: Identity {
        locked {
            Identity(id: stored.deviceId, name: deviceName, os: "mac", kind: kind,
                     code: stored.groupCode, key: key, tag: tag)
        }
    }

    var groupTag: String { locked { tag } }
    var groupCode: String { locked { stored.groupCode } }

    func setGroupCode(_ code: String) {
        locked {
            stored.groupCode = code
            recomputeKeys()
            save()
        }
    }

    var startAtLogin: Bool {
        get { locked { stored.startAtLogin ?? true } }
        set { locked { stored.startAtLogin = newValue; save() } }
    }

    var autoCopyDirect: Bool {
        get { locked { stored.autoCopyDirect ?? true } }
        set { locked { stored.autoCopyDirect = newValue; save() } }
    }

    var hoverReveal: Bool {
        get { locked { stored.hoverReveal ?? true } }
        set { locked { stored.hoverReveal = newValue; save() } }
    }

    var askedToMove: Bool {
        get { locked { stored.askedToMove ?? false } }
        set { locked { stored.askedToMove = newValue; save() } }
    }

    var welcomed: Bool {
        get { locked { stored.welcomed ?? false } }
        set { locked { stored.welcomed = newValue; save() } }
    }

    var placedShortcuts: Bool {
        get { locked { stored.placedShortcuts ?? false } }
        set { locked { stored.placedShortcuts = newValue; save() } }
    }

    var receiveFolder: URL {
        get {
            locked {
                if let p = stored.receiveFolder { return URL(fileURLWithPath: p, isDirectory: true) }
                return Paths.defaultReceive
            }
        }
        set { locked { stored.receiveFolder = newValue.path; save() } }
    }

    /// A Mac with an internal battery is a laptop.
    private static func detectKind() -> String {
        guard let info = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
              let list = IOPSCopyPowerSourcesList(info)?.takeRetainedValue() as? [CFTypeRef]
        else { return "desktop" }
        for source in list {
            if let desc = IOPSGetPowerSourceDescription(info, source)?.takeUnretainedValue() as? [String: Any],
               desc[kIOPSTypeKey] as? String == kIOPSInternalBatteryType {
                return "laptop"
            }
        }
        return "desktop"
    }
}

enum Log {
    private static let queue = DispatchQueue(label: "dropdock.log")
    private static let formatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    static func info(_ message: String) {
        let line = "\(formatter.string(from: Date())) \(message)\n"
        if Paths.isTest { FileHandle.standardError.write(Data(line.utf8)) }
        queue.async {
            let url = Paths.log
            if let size = (try? FileManager.default.attributesOfItem(atPath: url.path))?[.size] as? Int,
               size > 1_000_000 {
                let old = url.deletingPathExtension().appendingPathExtension("old.log")
                try? FileManager.default.removeItem(at: old)
                try? FileManager.default.moveItem(at: url, to: old)
            }
            if let handle = try? FileHandle(forWritingTo: url) {
                handle.seekToEndOfFile()
                handle.write(Data(line.utf8))
                try? handle.close()
            } else {
                try? Data(line.utf8).write(to: url)
            }
        }
    }
}
