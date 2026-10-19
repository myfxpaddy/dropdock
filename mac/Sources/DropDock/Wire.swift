import Foundation
import Network

enum WireError: Error, LocalizedError, Equatable {
    case closed
    case timeout
    case tooLarge
    case bad(String)
    case remote(String)
    case notLinked
    case auth
    case denied

    var errorDescription: String? {
        switch self {
        case .closed: return "The connection closed"
        case .timeout: return "The other computer stopped responding"
        case .tooLarge: return "The message was too large"
        case .bad(let s): return "Unexpected reply (\(s))"
        case .remote(let s): return s
        case .notLinked: return "Not linked with this computer"
        case .auth: return "The other computer has a different link key"
        case .denied: return "The link was declined"
        }
    }
}

final class Once: @unchecked Sendable {
    private let lock = NSLock()
    private var done = false

    func run(_ f: () -> Void) {
        lock.lock()
        if done { lock.unlock(); return }
        done = true
        lock.unlock()
        f()
    }
}

enum JSON {
    static func encode(_ obj: [String: Any]) throws -> Data {
        try JSONSerialization.data(withJSONObject: obj)
    }

    static func decode(_ data: Data) throws -> [String: Any] {
        guard let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw WireError.bad("not an object")
        }
        return obj
    }
}

func b64(_ any: Any?) -> Data? {
    guard let s = any as? String else { return nil }
    return Data(base64Encoded: s)
}

/// Length-prefixed frames over a Network.framework connection, with async reads and writes.
final class Wire: @unchecked Sendable {
    static let maxFrame = 48 << 20
    let conn: NWConnection
    private let queue = DispatchQueue(label: "dropdock.wire")

    init(_ conn: NWConnection) { self.conn = conn }

    convenience init(host: String, port: UInt16) {
        let params = NWParameters.tcp
        if let tcp = params.defaultProtocolStack.transportProtocol as? NWProtocolTCP.Options {
            tcp.noDelay = true
            tcp.connectionTimeout = 6
        }
        self.init(NWConnection(host: NWEndpoint.Host(host), port: NWEndpoint.Port(rawValue: port)!, using: params))
    }

    var remoteHost: String? {
        if case .hostPort(let host, _) = conn.endpoint { return "\(host)" }
        return nil
    }

    func open(timeout: Double) async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            let once = Once()
            conn.stateUpdateHandler = { [conn] state in
                switch state {
                case .ready:
                    once.run { cont.resume() }
                case .failed(let e), .waiting(let e):
                    once.run { cont.resume(throwing: e) }
                    conn.cancel()
                case .cancelled:
                    once.run { cont.resume(throwing: WireError.closed) }
                default:
                    break
                }
            }
            conn.start(queue: queue)
            queue.asyncAfter(deadline: .now() + timeout) { [conn] in
                once.run { cont.resume(throwing: WireError.timeout) }
                if conn.state != .ready { conn.cancel() }
            }
        }
    }

    func read(_ count: Int, timeout: Double) async throws -> Data {
        if count == 0 { return Data() }
        return try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Data, Error>) in
            let once = Once()
            let timer = DispatchWorkItem { [conn] in
                once.run { cont.resume(throwing: WireError.timeout) }
                conn.cancel()
            }
            queue.asyncAfter(deadline: .now() + timeout, execute: timer)
            conn.receive(minimumIncompleteLength: count, maximumLength: count) { data, _, _, error in
                timer.cancel()
                once.run {
                    if let error {
                        cont.resume(throwing: error)
                    } else if let data, data.count == count {
                        cont.resume(returning: data)
                    } else {
                        cont.resume(throwing: WireError.closed)
                    }
                }
            }
        }
    }

    func write(_ data: Data, timeout: Double = 60) async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            let once = Once()
            let timer = DispatchWorkItem { [conn] in
                once.run { cont.resume(throwing: WireError.timeout) }
                conn.cancel()
            }
            queue.asyncAfter(deadline: .now() + timeout, execute: timer)
            conn.send(content: data, completion: .contentProcessed { error in
                timer.cancel()
                once.run {
                    if let error { cont.resume(throwing: error) } else { cont.resume() }
                }
            })
        }
    }

    func readFrame(max: Int = Wire.maxFrame, timeout: Double = 60) async throws -> Data {
        let head = [UInt8](try await read(4, timeout: timeout))
        let len = Int(UInt32(head[0]) << 24 | UInt32(head[1]) << 16 | UInt32(head[2]) << 8 | UInt32(head[3]))
        guard len <= max else { throw WireError.tooLarge }
        return try await read(len, timeout: timeout)
    }

    func writeFrame(_ payload: Data) async throws {
        var frame = Data(capacity: payload.count + 4)
        let n = UInt32(payload.count)
        frame.append(contentsOf: [UInt8(n >> 24), UInt8(n >> 16 & 0xff), UInt8(n >> 8 & 0xff), UInt8(n & 0xff)])
        frame.append(payload)
        try await write(frame)
    }

    func readJSON(max: Int = 64 << 10, timeout: Double = 15) async throws -> [String: Any] {
        try JSON.decode(try await readFrame(max: max, timeout: timeout))
    }

    func writeJSON(_ obj: [String: Any]) async throws {
        try await writeFrame(try JSON.encode(obj))
    }

    func close() { conn.cancel() }
}

/// An authenticated, encrypted channel on top of a Wire.
final class SecureChannel: @unchecked Sendable {
    enum Message {
        case json([String: Any])
        case data(Data)
    }

    let wire: Wire
    private let sealer: Sealer
    private let opener: Opener

    init(wire: Wire, sealer: Sealer, opener: Opener) {
        self.wire = wire
        self.sealer = sealer
        self.opener = opener
    }

    func send(json obj: [String: Any]) async throws {
        var plain = Data([1])
        plain.append(try JSON.encode(obj))
        try await wire.writeFrame(try sealer.seal(plain))
    }

    func send(data: Data) async throws {
        var plain = Data(capacity: data.count + 1)
        plain.append(2)
        plain.append(data)
        try await wire.writeFrame(try sealer.seal(plain))
    }

    func receive(timeout: Double = 60) async throws -> Message {
        let plain = try opener.open(try await wire.readFrame(timeout: timeout))
        guard let type = plain.first else { throw WireError.bad("empty frame") }
        let body = plain.dropFirst()
        switch type {
        case 1: return .json(try JSON.decode(Data(body)))
        case 2: return .data(Data(body))
        default: throw WireError.bad("frame type \(type)")
        }
    }

    func receiveJSON(timeout: Double = 60) async throws -> [String: Any] {
        guard case .json(let obj) = try await receive(timeout: timeout) else { throw WireError.bad("expected message") }
        if obj["t"] as? String == "error" {
            throw WireError.remote(obj["message"] as? String ?? "The other computer reported an error")
        }
        return obj
    }

    func close() { wire.close() }
}
