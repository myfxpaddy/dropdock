// Draws the DropDock icon and writes PNGs at every size both platforms need.
// Usage: swift tools/make-icon.swift <output-folder>
import AppKit

let out = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "build/icon", isDirectory: true)
try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)

func draw(_ px: Int, full: Bool) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    let ctx = NSGraphicsContext(bitmapImageRep: rep)!
    NSGraphicsContext.current = ctx
    let s = CGFloat(px) / 1024
    let g = ctx.cgContext
    g.scaleBy(x: s, y: s)
    // Flip so y grows downward, like a design canvas.
    g.translateBy(x: 0, y: 1024)
    g.scaleBy(x: 1, y: -1)

    // macOS icons leave a margin; Windows icons use the full square.
    let body = full ? NSRect(x: 24, y: 24, width: 976, height: 976) : NSRect(x: 100, y: 100, width: 824, height: 824)
    let radius = body.width * 0.225
    let shape = NSBezierPath(roundedRect: body, xRadius: radius, yRadius: radius)
    NSGraphicsContext.saveGraphicsState()
    let shadow = NSShadow()
    shadow.shadowColor = NSColor(white: 0, alpha: 0.35)
    shadow.shadowBlurRadius = 24
    shadow.shadowOffset = NSSize(width: 0, height: -10)
    if !full { shadow.set() }
    NSGradient(colors: [NSColor(srgbRed: 0.17, green: 0.24, blue: 0.45, alpha: 1),
                        NSColor(srgbRed: 0.05, green: 0.07, blue: 0.14, alpha: 1)])!
        .draw(in: shape, angle: 90)
    NSGraphicsContext.restoreGraphicsState()

    let unit = body.width / 824
    let cx = body.midX

    // The bar along the top.
    let bar = NSRect(x: cx - 300 * unit, y: body.minY + 150 * unit, width: 600 * unit, height: 128 * unit)
    let barPath = NSBezierPath(roundedRect: bar, xRadius: 64 * unit, yRadius: 64 * unit)
    NSGraphicsContext.saveGraphicsState()
    let glow = NSShadow()
    glow.shadowColor = NSColor(srgbRed: 0.35, green: 0.75, blue: 1, alpha: 0.55)
    glow.shadowBlurRadius = 40 * unit
    glow.set()
    NSGradient(colors: [NSColor(srgbRed: 0.29, green: 0.83, blue: 1, alpha: 1),
                        NSColor(srgbRed: 0.48, green: 0.42, blue: 1, alpha: 1)])!
        .draw(in: barPath, angle: 0)
    NSGraphicsContext.restoreGraphicsState()

    // Three computers waiting in the bar.
    NSColor(white: 1, alpha: 0.92).setFill()
    for i in -1...1 {
        let x = cx + CGFloat(i) * 150 * unit
        NSBezierPath(roundedRect: NSRect(x: x - 42 * unit, y: bar.midY - 26 * unit, width: 84 * unit, height: 52 * unit),
                     xRadius: 10 * unit, yRadius: 10 * unit).fill()
    }

    // The arrow flying up into the bar.
    let arrow = NSBezierPath()
    arrow.lineWidth = 72 * unit
    arrow.lineCapStyle = .round
    arrow.lineJoinStyle = .round
    let tipY = body.minY + 360 * unit
    arrow.move(to: NSPoint(x: cx, y: body.minY + 690 * unit))
    arrow.line(to: NSPoint(x: cx, y: tipY))
    arrow.move(to: NSPoint(x: cx - 130 * unit, y: tipY + 130 * unit))
    arrow.line(to: NSPoint(x: cx, y: tipY))
    arrow.line(to: NSPoint(x: cx + 130 * unit, y: tipY + 130 * unit))
    NSColor.white.setStroke()
    arrow.stroke()

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

let iconset = out.appendingPathComponent("AppIcon.iconset", isDirectory: true)
try? FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
for (name, px) in [("16x16", 16), ("16x16@2x", 32), ("32x32", 32), ("32x32@2x", 64), ("128x128", 128),
                   ("128x128@2x", 256), ("256x256", 256), ("256x256@2x", 512), ("512x512", 512), ("512x512@2x", 1024)] {
    try! draw(px, full: false).write(to: iconset.appendingPathComponent("icon_\(name).png"))
}
let win = out.appendingPathComponent("windows", isDirectory: true)
try? FileManager.default.createDirectory(at: win, withIntermediateDirectories: true)
for px in [16, 20, 24, 32, 40, 48, 64, 128, 256] {
    try! draw(px, full: true).write(to: win.appendingPathComponent("\(px).png"))
}
try! draw(1024, full: false).write(to: out.appendingPathComponent("preview.png"))
print("icons written to \(out.path)")
