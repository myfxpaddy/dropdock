import AppKit

/// Everything needed to put DropDock on another computer. The prompts live in install/*.txt so the
/// apps and INSTALL.md always show the same words.
enum Setup {
    static let repo = "https://github.com/myfxpaddy/dropdock"
    static let guide = repo + "/blob/main/INSTALL.md"
    static let macCommand = "curl -fsSL https://raw.githubusercontent.com/myfxpaddy/dropdock/main/install/mac.sh | bash"
    static let windowsCommand = "irm https://raw.githubusercontent.com/myfxpaddy/dropdock/main/install/windows.ps1 | iex"

    static func prompt(_ name: String) -> String {
        if let url = Bundle.main.url(forResource: name, withExtension: "txt"),
           let text = try? String(contentsOf: url, encoding: .utf8) {
            return text
        }
        return "Install DropDock on this computer by following the guide at \(guide)."
    }
}

final class ClosureButton: NSButton {
    var handler: (() -> Void)?

    convenience init(title: String, handler: @escaping () -> Void) {
        self.init(frame: .zero)
        self.title = title
        self.handler = handler
        bezelStyle = .rounded
        target = self
        action = #selector(fire)
    }

    @objc private func fire() { handler?() }
}

/// "Set Up Another Computer": a Mac card and a Windows card, each with a prompt for Claude Code and a
/// one-line command.
@MainActor
final class SetupWindow: NSObject {
    static let shared = SetupWindow()
    private var window: NSWindow?

    func show() {
        BarController.shared.hide()
        if window == nil { window = build() }
        NSApp.activate(ignoringOtherApps: true)
        window?.center()
        window?.makeKeyAndOrderFront(nil)
    }

    /// Test sandbox only: renders the window to a PNG to check the layout.
    func snapshot(to url: URL) {
        guard Paths.isTest else { return }
        show()
        guard let view = window?.contentView, let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
        window?.orderOut(nil)
    }

    private func build() -> NSWindow {
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 860, height: 640),
                         styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
        w.title = "Set Up Another Computer"
        w.isReleasedWhenClosed = false

        let title = NSTextField(labelWithString: "Put DropDock on your other computers")
        title.font = .systemFont(ofSize: 20, weight: .semibold)
        let intro = NSTextField(wrappingLabelWithString:
            "On the other computer, open Claude Code and paste the prompt for that kind of computer. Claude installs DropDock, checks it works, and tells you what to click. Or run the one-line command yourself in Terminal (Mac) or PowerShell (Windows).")
        intro.textColor = .secondaryLabelColor

        let cards = NSStackView(views: [
            card(symbol: "apple.logo", name: "Mac", detail: "macOS 13 or newer, Apple Silicon or Intel",
                 prompt: Setup.prompt("prompt-mac"), command: Setup.macCommand, commandTitle: "Copy Terminal Command"),
            card(symbol: "pc", name: "Windows", detail: "Windows 10 or 11, no admin rights needed",
                 prompt: Setup.prompt("prompt-windows"), command: Setup.windowsCommand, commandTitle: "Copy PowerShell Command"),
        ])
        cards.orientation = .horizontal
        cards.distribution = .fillEqually
        cards.alignment = .top
        cards.spacing = 16

        let after = NSTextField(wrappingLabelWithString:
            "Once it is installed there, open the DropDock bar on that computer, click this computer (marked \"Click to link\"), check both screens show the same six-digit code, and click Allow here.")
        let guide = ClosureButton(title: "Open the Full Setup Guide") {
            if let url = URL(string: Setup.guide) { NSWorkspace.shared.open(url) }
        }

        let stack = NSStackView(views: [title, intro, cards, after, guide])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 14
        stack.edgeInsets = NSEdgeInsets(top: 24, left: 24, bottom: 24, right: 24)
        stack.translatesAutoresizingMaskIntoConstraints = false
        let content = NSView()
        content.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            stack.topAnchor.constraint(equalTo: content.topAnchor),
            stack.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            stack.widthAnchor.constraint(equalToConstant: 860),
            cards.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -48),
            intro.widthAnchor.constraint(equalTo: cards.widthAnchor),
            after.widthAnchor.constraint(equalTo: cards.widthAnchor),
        ])
        w.contentView = content
        w.setContentSize(content.fittingSize)
        return w
    }

    private func card(symbol: String, name: String, detail: String, prompt: String,
                      command: String, commandTitle: String) -> NSView {
        let icon = NSImageView(image: NSImage(systemSymbolName: symbol, accessibilityDescription: name) ?? NSImage())
        icon.symbolConfiguration = .init(pointSize: 26, weight: .regular)
        icon.contentTintColor = .labelColor
        let nameLabel = NSTextField(labelWithString: name)
        nameLabel.font = .systemFont(ofSize: 16, weight: .semibold)
        let detailLabel = NSTextField(labelWithString: detail)
        detailLabel.font = .systemFont(ofSize: 11)
        detailLabel.textColor = .secondaryLabelColor
        let names = NSStackView(views: [nameLabel, detailLabel])
        names.orientation = .vertical
        names.alignment = .leading
        names.spacing = 2
        let header = NSStackView(views: [icon, names])
        header.spacing = 10

        let promptTitle = NSTextField(labelWithString: "Prompt for Claude Code on that \(name == "Mac" ? "Mac" : "PC")")
        promptTitle.font = .systemFont(ofSize: 12, weight: .semibold)

        let scroll = NSTextView.scrollableTextView()
        scroll.borderType = .bezelBorder
        scroll.hasVerticalScroller = true
        if let text = scroll.documentView as? NSTextView {
            text.string = prompt
            text.isEditable = false
            text.isSelectable = true
            text.font = .monospacedSystemFont(ofSize: 11, weight: .regular)
            text.textContainerInset = NSSize(width: 6, height: 6)
            text.setAccessibilityLabel("\(name) prompt for Claude Code")
        }
        scroll.translatesAutoresizingMaskIntoConstraints = false
        scroll.heightAnchor.constraint(equalToConstant: 260).isActive = true

        var copyPrompt: ClosureButton!
        copyPrompt = ClosureButton(title: "Copy Prompt") { SetupWindow.copy(prompt, button: copyPrompt, title: "Copy Prompt") }
        var copyCommand: ClosureButton!
        copyCommand = ClosureButton(title: commandTitle) { SetupWindow.copy(command, button: copyCommand, title: commandTitle) }
        let buttons = NSStackView(views: [copyPrompt, copyCommand])
        buttons.spacing = 8

        let commandLabel = NSTextField(labelWithString: command)
        commandLabel.font = .monospacedSystemFont(ofSize: 10, weight: .regular)
        commandLabel.textColor = .secondaryLabelColor
        commandLabel.isSelectable = true
        commandLabel.lineBreakMode = .byTruncatingMiddle

        let stack = NSStackView(views: [header, promptTitle, scroll, buttons, commandLabel])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 10
        stack.edgeInsets = NSEdgeInsets(top: 14, left: 14, bottom: 14, right: 14)
        stack.translatesAutoresizingMaskIntoConstraints = false

        let box = NSBox()
        box.boxType = .custom
        box.cornerRadius = 10
        box.borderColor = .separatorColor
        box.fillColor = .controlBackgroundColor
        box.contentViewMargins = .zero
        box.contentView?.addSubview(stack)
        if let inner = box.contentView {
            NSLayoutConstraint.activate([
                stack.leadingAnchor.constraint(equalTo: inner.leadingAnchor),
                stack.trailingAnchor.constraint(equalTo: inner.trailingAnchor),
                stack.topAnchor.constraint(equalTo: inner.topAnchor),
                stack.bottomAnchor.constraint(equalTo: inner.bottomAnchor),
                scroll.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -28),
                commandLabel.widthAnchor.constraint(lessThanOrEqualTo: stack.widthAnchor, constant: -28),
            ])
        }
        return box
    }

    private static func copy(_ text: String, button: NSButton, title: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        button.title = "Copied"
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { button.title = title }
    }
}
