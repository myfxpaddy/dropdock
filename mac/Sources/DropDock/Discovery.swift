import Darwin
import Foundation
import Network

struct Beacon {
    let type: String
    let id: String
    let name: String
    let os: String
    let kind: String
    let port: UInt16
    let tag: String
    let generalTime: Int64
    let address: String
}

/// Finds other DropDock computers with small UDP broadcasts. Event driven: the only timer is a
/// heartbeat every 50 seconds.
final class Discovery {
    static let port: UInt16 = 47820

    var onBeacon: ((Beacon) -> Void)?
    var fields: () -> [String: Any] = { [:] }

    private var fd: Int32 = -1
    private var source: DispatchSourceRead?
    private var heartbeat: DispatchSourceTimer?
    private var replyPending = false
    private var pathMonitor: NWPathMonitor?
    private var pathDebounce: DispatchWorkItem?
    private var sawFirstPath = false

    func start() {
        fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard fd >= 0 else { Log.info("discovery: socket failed \(errno)"); return }
        var yes: Int32 = 1
        let size = socklen_t(MemoryLayout<Int32>.size)
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &yes, size)
        setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &yes, size)
        setsockopt(fd, SOL_SOCKET, SO_BROADCAST, &yes, size)

        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = Discovery.port.bigEndian
        addr.sin_addr = in_addr(s_addr: 0)
        let bound = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        if bound != 0 { Log.info("discovery: bind failed \(errno)") }
        _ = fcntl(fd, F_SETFL, O_NONBLOCK)

        let src = DispatchSource.makeReadSource(fileDescriptor: fd, queue: .main)
        src.setEventHandler { [weak self] in self?.drain() }
        src.resume()
        source = src

        let timer = DispatchSource.makeTimerSource(queue: .main)
        timer.schedule(deadline: .now() + 50, repeating: 50, leeway: .seconds(5))
        timer.setEventHandler { [weak self] in self?.send("here") }
        timer.resume()
        heartbeat = timer

        let monitor = NWPathMonitor()
        monitor.pathUpdateHandler = { [weak self] _ in
            DispatchQueue.main.async { self?.networkChanged() }
        }
        monitor.start(queue: .main)
        pathMonitor = monitor

        send("hello")
    }

    /// Ask everyone to announce themselves (after wake, network change or a new link).
    func announce() { send("hello") }

    func goodbye() { send("bye") }

    private func networkChanged() {
        if !sawFirstPath { sawFirstPath = true; return }
        pathDebounce?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.send("hello") }
        pathDebounce = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5, execute: work)
    }

    private func drain() {
        var buf = [UInt8](repeating: 0, count: 2048)
        while true {
            var from = sockaddr_in()
            var len = socklen_t(MemoryLayout<sockaddr_in>.size)
            let n = withUnsafeMutablePointer(to: &from) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    recvfrom(fd, &buf, buf.count, 0, $0, &len)
                }
            }
            if n <= 0 { break }
            let ip = String(cString: inet_ntoa(from.sin_addr))
            handle(Data(buf[0..<n]), from: ip)
        }
    }

    private func handle(_ data: Data, from ip: String) {
        guard let obj = try? JSON.decode(data),
              obj["app"] as? String == "dropdock",
              (obj["v"] as? NSNumber)?.intValue == 1,
              let type = obj["t"] as? String,
              let id = obj["id"] as? String,
              id != Settings.shared.identity.id
        else { return }
        let beacon = Beacon(
            type: type, id: id,
            name: (obj["name"] as? String) ?? "Computer",
            os: (obj["os"] as? String) ?? "mac",
            kind: (obj["kind"] as? String) ?? "desktop",
            port: UInt16(truncatingIfNeeded: (obj["port"] as? NSNumber)?.intValue ?? 47821),
            tag: (obj["tag"] as? String) ?? "",
            generalTime: (obj["gt"] as? NSNumber)?.int64Value ?? 0,
            address: ip)
        onBeacon?(beacon)
        if type == "hello" && !replyPending {
            replyPending = true
            let delay = Double.random(in: 0.05...0.4)
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                self?.replyPending = false
                self?.send("here")
            }
        }
    }

    private func send(_ type: String) {
        guard fd >= 0 else { return }
        var obj = fields()
        obj["app"] = "dropdock"
        obj["v"] = 1
        obj["t"] = type
        guard let data = try? JSON.encode(obj) else { return }
        for target in broadcastTargets() {
            var addr = sockaddr_in()
            addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            addr.sin_family = sa_family_t(AF_INET)
            addr.sin_port = Discovery.port.bigEndian
            addr.sin_addr = in_addr(s_addr: target)
            _ = data.withUnsafeBytes { bytes in
                withUnsafePointer(to: &addr) {
                    $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                        sendto(fd, bytes.baseAddress, data.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                    }
                }
            }
        }
    }

    private func broadcastTargets() -> [in_addr_t] {
        var targets: Set<in_addr_t> = [0xFFFF_FFFF]
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0 else { return Array(targets) }
        defer { freeifaddrs(list) }
        var cursor = list
        while let ifa = cursor {
            let flags = Int32(ifa.pointee.ifa_flags)
            if flags & IFF_UP != 0, flags & IFF_BROADCAST != 0, flags & IFF_LOOPBACK == 0,
               let addr = ifa.pointee.ifa_addr, addr.pointee.sa_family == sa_family_t(AF_INET),
               let bcast = ifa.pointee.ifa_dstaddr {
                let sin = bcast.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee }
                if sin.sin_addr.s_addr != 0 { targets.insert(sin.sin_addr.s_addr) }
            }
            cursor = ifa.pointee.ifa_next
        }
        return Array(targets)
    }
}
