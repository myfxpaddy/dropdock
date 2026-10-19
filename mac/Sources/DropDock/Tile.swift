import AppKit

struct TileModel {
    var key: String
    var symbol: String
    var title: String
    var subtitle: String
    var width: CGFloat = 100
    var wide = false
    var compact = false
    var progress: Double?
    var highlight = false
    var dimmed = false
    var subtitleColor: NSColor?
    var actionTitle: String?
    var acceptsDrops = true
    var help: String
}

/// A small filled button. Standard buttons draw as inactive in a panel that never takes focus, so
/// this draws itself, works on the first click and keeps readable contrast for any accent color.
final class PillButton: NSView {
    var onPress: (() -> Void)?
    private let label = NSTextField(labelWithString: "")
    private let fontSize: CGFloat

    var title: String {
        get { label.stringValue }
        set { label.stringValue = newValue; setAccessibilityLabel(newValue) }
    }

    init(title: String = "", fontSize: CGFloat = 11) {
        self.fontSize = fontSize
        super.init(frame: .zero)
        wantsLayer = true
        layer?.cornerCurve = .continuous
        label.font = .systemFont(ofSize: fontSize, weight: .semibold)
        label.alignment = .center
        addSubview(label)
        self.title = title
        recolor()
    }

    required init?(coder: NSCoder) { fatalError() }

    /// Accent darkened a little so white text passes WCAG AA; black text if the accent is very light.
    private func recolor() {
        let base = (NSColor.controlAccentColor.usingColorSpace(.sRGB) ?? .systemBlue)
        let fill = base.blended(withFraction: 0.22, of: .black) ?? base
        let lum = 0.2126 * fill.redComponent + 0.7152 * fill.greenComponent + 0.0722 * fill.blueComponent
        layer?.backgroundColor = fill.cgColor
        label.textColor = lum > 0.6 ? .black : .white
    }

    var fittingWidth: CGFloat { ceil(label.intrinsicContentSize.width) + 22 }

    override func layout() {
        super.layout()
        layer?.cornerRadius = bounds.height / 2
        let h = ceil(label.intrinsicContentSize.height)
        label.frame = NSRect(x: 0, y: (bounds.height - h) / 2, width: bounds.width, height: h)
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { frame.contains(point) ? self : nil }
    override func mouseDown(with event: NSEvent) { layer?.opacity = 0.7 }

    override func mouseUp(with event: NSEvent) {
        layer?.opacity = 1
        if bounds.contains(convert(event.locationInWindow, from: nil)) { onPress?() }
    }

    override func viewDidChangeEffectiveAppearance() { recolor() }
    override func isAccessibilityElement() -> Bool { true }
    override func accessibilityRole() -> NSAccessibility.Role? { .button }
    override func accessibilityPerformPress() -> Bool { onPress?(); return true }
}

/// One square in the bar: a computer, General, or the Paste button.
final class TileView: NSView {
    static let height: CGFloat = 84

    private(set) var model: TileModel
    var onClick: (() -> Void)?
    var onAction: (() -> Void)?
    var onDrop: ((NSDraggingInfo) -> Bool)?
    var onDragEnter: (() -> Void)?

    private let iconView = NSImageView()
    private let titleLabel = NSTextField(labelWithString: "")
    private let subtitleLabel = NSTextField(labelWithString: "")
    private let track = CALayer()
    private let fill = CALayer()
    private let actionButton = PillButton()
    private var hovering = false
    private var dragOver = false
    private var pressed = false
    private var flashText: String?
    private var flashWork: DispatchWorkItem?

    init(_ model: TileModel) {
        self.model = model
        super.init(frame: NSRect(x: 0, y: 0, width: model.width, height: TileView.height))
        wantsLayer = true
        layer?.cornerRadius = 12
        layer?.cornerCurve = .continuous
        layer?.borderWidth = 1.5

        iconView.imageScaling = .scaleProportionallyUpOrDown
        iconView.contentTintColor = .white
        titleLabel.font = .systemFont(ofSize: 12, weight: .semibold)
        titleLabel.textColor = .white
        titleLabel.lineBreakMode = .byTruncatingTail
        subtitleLabel.font = .systemFont(ofSize: 10.5)
        subtitleLabel.lineBreakMode = .byTruncatingTail
        subtitleLabel.cell?.truncatesLastVisibleLine = true
        [iconView, titleLabel, subtitleLabel].forEach(addSubview)

        track.backgroundColor = NSColor(white: 1, alpha: 0.18).cgColor
        track.cornerRadius = 1.5
        fill.backgroundColor = NSColor.controlAccentColor.cgColor
        fill.cornerRadius = 1.5
        track.addSublayer(fill)
        layer?.addSublayer(track)

        actionButton.onPress = { [weak self] in self?.onAction?() }
        addSubview(actionButton)

        registerForDraggedTypes(BarController.dragTypes)
        update(model)
    }

    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }

    func update(_ m: TileModel) {
        model = m
        iconView.image = NSImage(systemSymbolName: m.symbol, accessibilityDescription: nil)?
            .withSymbolConfiguration(.init(pointSize: m.compact ? 17 : (m.wide ? 20 : 24), weight: .regular))
        titleLabel.stringValue = m.title
        subtitleLabel.stringValue = flashText ?? m.subtitle
        subtitleLabel.textColor = flashText != nil ? .white : (m.subtitleColor ?? NSColor(white: 1, alpha: 0.72))
        subtitleLabel.maximumNumberOfLines = m.wide ? 2 : 1
        subtitleLabel.cell?.wraps = m.wide
        titleLabel.alignment = m.wide ? .left : .center
        subtitleLabel.alignment = m.wide ? .left : .center
        actionButton.title = m.actionTitle ?? ""
        actionButton.isHidden = m.actionTitle == nil
        alphaValue = m.dimmed ? 0.6 : 1
        toolTip = m.help
        setFrameSize(NSSize(width: m.width, height: TileView.height))
        needsLayout = true
        refreshLook()
    }

    /// Shows a short message in place of the subtitle.
    func flash(_ text: String) {
        flashWork?.cancel()
        flashText = text
        update(model)
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.flashText = nil
            self.update(self.model)
        }
        flashWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.6, execute: work)
    }

    override func layout() {
        super.layout()
        let w = bounds.width
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        titleLabel.isHidden = model.compact
        if model.compact {
            iconView.frame = NSRect(x: (w - 22) / 2, y: (TileView.height - 22) / 2, width: 22, height: 22)
            subtitleLabel.isHidden = true
            actionButton.isHidden = true
            track.isHidden = true
        } else if model.wide {
            iconView.frame = NSRect(x: 12, y: 12, width: 24, height: 22)
            titleLabel.frame = NSRect(x: 44, y: 12, width: w - 56, height: 17)
            let hasButton = !actionButton.isHidden
            subtitleLabel.frame = NSRect(x: 44, y: 31, width: w - 56, height: hasButton ? 28 : 40)
            let bw = max(actionButton.fittingWidth, 54)
            actionButton.frame = NSRect(x: w - bw - 10, y: TileView.height - 27, width: bw, height: 20)
            track.isHidden = true
        } else {
            iconView.frame = NSRect(x: (w - 30) / 2, y: 9, width: 30, height: 28)
            titleLabel.frame = NSRect(x: 6, y: 40, width: w - 12, height: 16)
            subtitleLabel.frame = NSRect(x: 6, y: 57, width: w - 12, height: 14)
            let bw = max(actionButton.fittingWidth, 54)
            actionButton.frame = NSRect(x: (w - bw) / 2, y: 56, width: bw, height: 20)
            subtitleLabel.isHidden = !actionButton.isHidden && flashText == nil
            if let p = model.progress {
                track.isHidden = false
                track.frame = CGRect(x: 12, y: TileView.height - 9, width: w - 24, height: 3)
                fill.frame = CGRect(x: 0, y: 0, width: (w - 24) * CGFloat(max(0.02, min(1, p))), height: 3)
            } else {
                track.isHidden = true
            }
        }
        if model.wide { subtitleLabel.isHidden = false }
        CATransaction.commit()
    }

    private func refreshLook() {
        let accent = NSColor.controlAccentColor
        var bg = NSColor(white: 1, alpha: 0.07)
        var border = NSColor.clear
        if model.highlight {
            bg = accent.withAlphaComponent(0.2)
            border = accent.withAlphaComponent(0.9)
        }
        if hovering { bg = model.highlight ? accent.withAlphaComponent(0.35) : NSColor(white: 1, alpha: 0.14) }
        if pressed { bg = NSColor(white: 1, alpha: 0.2) }
        if dragOver {
            bg = accent.withAlphaComponent(0.5)
            border = accent
        }
        CATransaction.begin()
        CATransaction.setDisableActions(NSWorkspace.shared.accessibilityDisplayShouldReduceMotion)
        layer?.backgroundColor = bg.cgColor
        layer?.borderColor = border.cgColor
        CATransaction.commit()
    }

    // MARK: Mouse

    override func hitTest(_ point: NSPoint) -> NSView? {
        let local = convert(point, from: superview)
        guard bounds.contains(local) else { return nil }
        if !actionButton.isHidden, actionButton.frame.contains(local) { return actionButton }
        return self
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                                       owner: self, userInfo: nil))
    }

    override func mouseEntered(with event: NSEvent) { hovering = true; refreshLook() }
    override func mouseExited(with event: NSEvent) { hovering = false; pressed = false; refreshLook() }
    override func mouseDown(with event: NSEvent) { pressed = true; refreshLook() }

    override func mouseUp(with event: NSEvent) {
        pressed = false
        refreshLook()
        if bounds.contains(convert(event.locationInWindow, from: nil)) { onClick?() }
    }

    // MARK: Drops

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        onDragEnter?()
        guard model.acceptsDrops else { return [] }
        dragOver = true
        refreshLook()
        return .copy
    }

    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation {
        model.acceptsDrops ? .copy : []
    }

    override func draggingExited(_ sender: NSDraggingInfo?) { dragOver = false; refreshLook() }
    override func draggingEnded(_ sender: NSDraggingInfo) { dragOver = false; refreshLook() }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        dragOver = false
        refreshLook()
        return onDrop?(sender) ?? false
    }

    // MARK: Accessibility

    override func isAccessibilityElement() -> Bool { true }
    override func accessibilityRole() -> NSAccessibility.Role? { .button }
    override func accessibilityLabel() -> String? {
        model.compact ? model.help : "\(model.title). \(flashText ?? model.subtitle)"
    }
    override func accessibilityHelp() -> String? { model.help }

    override func accessibilityPerformPress() -> Bool {
        onClick?()
        return true
    }
}
