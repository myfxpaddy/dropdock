import AppKit

/// Command line mode, used by the interop tests and handy for scripts:
///   DropDock --cli info
///   DropDock --cli serve
///   DropDock --cli send <host> <port> <file>...
///   DropDock --cli clip <host> <port> <general|direct> <text> [html]
///   DropDock --cli link <host> <port>
///   DropDock --cli set-code <code>
enum CLI {
    nonisolated(unsafe) static var active = false

    private final class Box: @unchecked Sendable { var code: Int32 = 0 }

    static func run(_ args: [String]) -> Int32 {
        active = true
        guard let cmd = args.first else { return usage() }
        if cmd == "serve" {
            // Headless receiver for tests: discovery and server, no windows.
            MainActor.assumeIsolated { Hub.shared.start() }
            RunLoop.main.run()
            return 0
        }
        let box = Box()
        let done = DispatchSemaphore(value: 0)
        Task.detached {
            do {
                switch cmd {
                case "info":
                    let me = Settings.shared.identity
                    print("{\"id\":\"\(me.id)\",\"name\":\"\(me.name)\",\"tag\":\"\(me.tag)\",\"code\":\"\(me.code)\"}")
                case "set-code":
                    guard args.count == 2, let code = DDCrypto.normalize(code: args[1]) else { box.code = 2; break }
                    Settings.shared.setGroupCode(code)
                    print("tag \(Settings.shared.groupTag)")
                case "send":
                    guard args.count >= 4, let port = UInt16(args[2]) else { box.code = 2; break }
                    let m = try Manifest.build(args[3...].map { URL(fileURLWithPath: $0) })
                    let saved = try await Client.sendFiles(m, host: args[1], port: port) { _ in }
                    print("saved \(saved.joined(separator: ", "))")
                case "clip":
                    guard args.count >= 5, let port = UInt16(args[2]) else { box.code = 2; break }
                    var clip = Clip()
                    let me = Settings.shared.identity
                    clip.scope = args[3] == "direct" ? "direct" : "general"
                    clip.from = me.id
                    clip.fromName = me.name
                    clip.text = args[4]
                    if args.count >= 6 { clip.html = args[5] }
                    try await Client.sendClip(clip, host: args[1], port: port)
                    print("ok")
                case "link":
                    guard args.count == 3, let port = UInt16(args[2]) else { box.code = 2; break }
                    let code = try await Client.requestLink(host: args[1], port: port) { sas in print("code \(sas)") }
                    Settings.shared.setGroupCode(code)
                    print("linked, tag \(Settings.shared.groupTag)")
                default:
                    box.code = 2
                }
            } catch {
                FileHandle.standardError.write(Data("error: \(error.localizedDescription) \(error)\n".utf8))
                box.code = 1
            }
            done.signal()
        }
        done.wait()
        if box.code == 2 { return usage() }
        return box.code
    }

    private static func usage() -> Int32 {
        FileHandle.standardError.write(Data("usage: DropDock --cli info | set-code <code> | send <host> <port> <files> | clip <host> <port> <general|direct> <text> [html] | link <host> <port>\n".utf8))
        return 2
    }
}
