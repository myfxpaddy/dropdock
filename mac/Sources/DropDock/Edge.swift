import AppKit

/// Watches for the pointer reaching the top edge of a screen: resting there opens the bar, and so does
/// dragging something there. AppKit hands over mouse events only as they happen, and almost all of them
/// are rejected by a single height comparison, so this costs nothing while the mouse is still.
@MainActor
final class EdgeWatcher {
    static let shared = EdgeWatcher()

    private var monitor: Any?
    private var baseline = 0
    private var lowestTop: CGFloat = 0
    private var restingAt: NSPoint?
    private var dwell: DispatchWorkItem?

    func start() {
        monitor = NSEvent.addGlobalMonitorForEvents(matching: [.mouseMoved, .leftMouseDown, .leftMouseDragged]) { event in
            let type = event.type
            MainActor.assumeIsolated { EdgeWatcher.shared.handle(type) }
        }
        refreshScreens()
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                               object: nil, queue: .main) { _ in
            Task { @MainActor in EdgeWatcher.shared.refreshScreens() }
        }
    }

    private func refreshScreens() {
        lowestTop = NSScreen.screens.map(\.frame.maxY).min() ?? 0
    }

    private func handle(_ type: NSEvent.EventType) {
        let bar = BarController.shared
        switch type {
        case .mouseMoved:
            hover()
        case .leftMouseDown:
            stopResting()
            baseline = NSPasteboard(name: .drag).changeCount
            bar.globalMouseDown(NSEvent.mouseLocation)
        case .leftMouseDragged:
            stopResting()
            guard !bar.isVisible else { return }
            let p = NSEvent.mouseLocation
            guard EdgeWatcher.atTopEdge(p, within: 6) else { return }
            // Only a real drag and drop changes the drag pasteboard; moving a window does not.
            guard NSPasteboard(name: .drag).changeCount != baseline else { return }
            bar.reveal(reason: .drag, at: p)
        default:
            break
        }
    }

    /// Opens the bar once the pointer has rested at a top edge for a moment. Sliding along the edge
    /// (on the way to a menu, say) keeps restarting the wait, so it only opens when you stop.
    private func hover() {
        let p = NSEvent.mouseLocation
        guard p.y >= lowestTop - 4 else {
            if restingAt != nil { stopResting() }
            return
        }
        guard Settings.shared.hoverReveal, !BarController.shared.isVisible, EdgeWatcher.atTopEdge(p, within: 3) else {
            stopResting()
            return
        }
        if let r = restingAt, abs(r.x - p.x) < 6 { return }
        restingAt = p
        dwell?.cancel()
        let work = DispatchWorkItem { Task { @MainActor in EdgeWatcher.shared.rested() } }
        dwell = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3, execute: work)
    }

    private func rested() {
        let p = NSEvent.mouseLocation
        defer { stopResting() }
        guard let r = restingAt, abs(r.x - p.x) < 10, NSEvent.pressedMouseButtons == 0,
              EdgeWatcher.atTopEdge(p, within: 3) else { return }
        BarController.shared.reveal(reason: .hover, at: p)
    }

    private func stopResting() {
        dwell?.cancel()
        dwell = nil
        restingAt = nil
    }

    /// True near the top of a screen that has no other screen directly above it.
    static func atTopEdge(_ p: NSPoint, within margin: CGFloat) -> Bool {
        guard let s = NSScreen.screens.first(where: {
            p.x >= $0.frame.minX && p.x < $0.frame.maxX && p.y >= $0.frame.minY && p.y <= $0.frame.maxY
        }) else { return false }
        guard p.y >= s.frame.maxY - margin else { return false }
        let above = NSPoint(x: p.x, y: s.frame.maxY + 3)
        return !NSScreen.screens.contains { NSMouseInRect(above, $0.frame, false) }
    }
}

/// Small notices in the top right corner, like system notifications but with no permission needed.
@MainActor
final class Toast {
    static let shared = Toast()

    private var panel: NSPanel?
    private var hideWork: DispatchWorkItem?
    private var action: (() -> Void)?

    func show(symbol: String, title: String, body: String, actionTitle: String? = nil,
              seconds: Double? = nil, action: (() -> Void)? = nil) {
        guard NSApp != nil, !CLI.active else {
            print("\(title): \(body)")
            return
        }
        hideWork?.cancel()
        panel?.orderOut(nil)
        self.action = action

        let width: CGFloat = 340
        let textWidth = width - 58 - (actionTitle != nil ? 70 : 12)
        let titleLabel = NSTextField(labelWithString: title)
        titleLabel.font = .systemFont(ofSize: 13, weight: .semibold)
        titleLabel.textColor = .white
        titleLabel.lineBreakMode = .byTruncatingTail
        let bodyLabel = NSTextField(wrappingLabelWithString: body)
        bodyLabel.font = .systemFont(ofSize: 12)
        bodyLabel.textColor = NSColor(white: 1, alpha: 0.8)
        bodyLabel.maximumNumberOfLines = 4
        bodyLabel.preferredMaxLayoutWidth = textWidth
        let bodyHeight = min(66, ceil(bodyLabel.sizeThatFits(NSSize(width: textWidth, height: 200)).height))
        let height = max(64, 14 + 18 + 2 + bodyHeight + 14)

        let effect = ToastView(frame: NSRect(x: 0, y: 0, width: width, height: height))
        effect.material = .hudWindow
        effect.blendingMode = .behindWindow
        effect.state = .active
        effect.maskImage = BarController.roundedMask(radius: 14)
        effect.onClick = { [weak self] in self?.activate() }

        let icon = NSImageView(frame: NSRect(x: 14, y: height - 14 - 26, width: 28, height: 26))
        icon.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)?
            .withSymbolConfiguration(.init(pointSize: 20, weight: .regular))
        icon.contentTintColor = .controlAccentColor
        effect.addSubview(icon)
        titleLabel.frame = NSRect(x: 52, y: height - 14 - 18, width: textWidth, height: 18)
        effect.addSubview(titleLabel)
        bodyLabel.frame = NSRect(x: 52, y: height - 14 - 18 - 2 - bodyHeight, width: textWidth, height: bodyHeight)
        effect.addSubview(bodyLabel)
        if let actionTitle {
            let button = PillButton(title: actionTitle, fontSize: 12)
            button.onPress = { [weak self] in self?.activate() }
            let bw = max(button.fittingWidth, 58)
            button.frame = NSRect(x: width - bw - 12, y: (height - 24) / 2, width: bw, height: 24)
            effect.addSubview(button)
        }

        let screen = NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main
        guard let vf = screen?.visibleFrame else { return }
        let rect = NSRect(x: vf.maxX - width - 12, y: vf.maxY - height - 12, width: width, height: height)
        let p = BarPanel(contentRect: rect, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.level = .popUpMenu
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        p.backgroundColor = .clear
        p.isOpaque = false
        p.hasShadow = true
        p.hidesOnDeactivate = false
        p.isReleasedWhenClosed = false
        p.appearance = NSAppearance(named: .vibrantDark)
        p.contentView = effect
        p.alphaValue = 0
        p.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.18
            p.animator().alphaValue = 1
        }
        NSAccessibility.post(element: effect, notification: .announcementRequested,
                             userInfo: [.announcement: "\(title). \(body)", .priority: NSAccessibilityPriorityLevel.medium.rawValue])
        panel = p

        let work = DispatchWorkItem { [weak self] in self?.dismiss() }
        hideWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + (seconds ?? (action != nil ? 7 : 4.5)), execute: work)
    }

    private func activate() {
        let a = action
        dismiss()
        a?()
    }

    func dismiss() {
        hideWork?.cancel()
        guard let p = panel else { return }
        panel = nil
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.15
            p.animator().alphaValue = 0
        }, completionHandler: {
            Task { @MainActor in p.orderOut(nil) }
        })
    }
}

final class ToastView: NSVisualEffectView {
    var onClick: (() -> Void)?
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseUp(with event: NSEvent) { onClick?() }
}
