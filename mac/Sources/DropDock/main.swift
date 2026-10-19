import AppKit

let arguments = CommandLine.arguments
if arguments.count > 1 && arguments[1] == "--cli" {
    exit(CLI.run(Array(arguments.dropFirst(2))))
}

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    app.run()
}
