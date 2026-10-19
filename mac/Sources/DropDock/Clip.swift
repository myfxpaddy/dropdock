import AppKit

/// A piece of copied content in every format we could read, so formatting survives the trip.
struct Clip {
    static let maxImageBytes = 20 << 20

    var id = UUID().uuidString.lowercased()
    var scope = "general"
    var from = ""
    var fromName = ""
    var time = Int64(Date().timeIntervalSince1970 * 1000)
    var text: String?
    var html: String?
    var rtf: Data?
    var png: Data?

    var isEmpty: Bool { text == nil && html == nil && rtf == nil && png == nil }

    /// One short line for tiles and menus.
    var preview: String {
        if let t = text?.trimmingCharacters(in: .whitespacesAndNewlines), !t.isEmpty {
            let line = t.split(whereSeparator: \.isNewline).first.map(String.init) ?? t
            return line.count > 80 ? String(line.prefix(80)) + "..." : line
        }
        if png != nil { return "Image" }
        if html != nil || rtf != nil { return "Formatted text" }
        return "Empty"
    }

    var json: [String: Any] {
        var o: [String: Any] = ["id": id, "scope": scope, "from": from, "fromName": fromName, "time": time]
        if let text { o["text"] = text }
        if let html { o["html"] = html }
        if let rtf { o["rtf"] = rtf.base64EncodedString() }
        if let png { o["png"] = png.base64EncodedString() }
        return o
    }

    init() {}

    init?(json o: [String: Any]?) {
        guard let o else { return nil }
        id = o["id"] as? String ?? UUID().uuidString.lowercased()
        scope = o["scope"] as? String == "direct" ? "direct" : "general"
        from = o["from"] as? String ?? ""
        fromName = o["fromName"] as? String ?? ""
        time = (o["time"] as? NSNumber)?.int64Value ?? 0
        text = o["text"] as? String
        html = o["html"] as? String
        rtf = b64(o["rtf"])
        png = b64(o["png"])
        if isEmpty { return nil }
    }

    /// Reads the clipboard (or a drag pasteboard). Keeps plain text, HTML and RTF; keeps an image
    /// only when there is no text, so copying cells or a page does not drag a picture along.
    static func read(from pb: NSPasteboard) -> Clip? {
        var clip = Clip()
        clip.text = pb.string(forType: .string)
        clip.html = pb.string(forType: .html)
        clip.rtf = pb.data(forType: .rtf)
        if clip.rtf == nil, let rtfd = pb.data(forType: .rtfd),
           let attr = NSAttributedString(rtfd: rtfd, documentAttributes: nil) {
            clip.rtf = attr.rtf(from: NSRange(location: 0, length: attr.length), documentAttributes: [:])
        }
        // Windows apps and browsers understand HTML better than RTF, so make sure there is some.
        if clip.html == nil, let rtf = clip.rtf,
           let attr = NSAttributedString(rtf: rtf, documentAttributes: nil),
           let data = try? attr.data(from: NSRange(location: 0, length: attr.length),
                                     documentAttributes: [.documentType: NSAttributedString.DocumentType.html,
                                                          .characterEncoding: String.Encoding.utf8.rawValue]) {
            clip.html = String(data: data, encoding: .utf8)
        }
        if clip.text == nil && clip.html == nil && clip.rtf == nil {
            if let png = pb.data(forType: .png) {
                clip.png = png
            } else if let tiff = pb.data(forType: .tiff), let rep = NSBitmapImageRep(data: tiff) {
                clip.png = rep.representation(using: .png, properties: [:])
            }
            if let png = clip.png, png.count > maxImageBytes { clip.png = nil }
        }
        return clip.isEmpty ? nil : clip
    }

    /// Puts every format on the clipboard so each app can take the richest one it understands.
    func write(to pb: NSPasteboard = .general) {
        var plain = text
        if plain == nil, let html, let data = html.data(using: .utf8),
           let attr = NSAttributedString(html: data, documentAttributes: nil) {
            plain = attr.string
        }
        if plain == nil, let rtf, let attr = NSAttributedString(rtf: rtf, documentAttributes: nil) {
            plain = attr.string
        }
        pb.clearContents()
        if let rtf { pb.setData(rtf, forType: .rtf) }
        if let html { pb.setString(html, forType: .html) }
        if let plain { pb.setString(plain, forType: .string) }
        if let png {
            pb.setData(png, forType: .png)
            if let rep = NSBitmapImageRep(data: png), let tiff = rep.tiffRepresentation {
                pb.setData(tiff, forType: .tiff)
            }
        }
    }
}
