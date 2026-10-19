import CommonCrypto
import CryptoKit
import Foundation

enum CryptoError: Error {
    case cipher
    case badTag
    case badKey
}

/// Primitives shared with the Windows build. The wire format is described in README.md (Protocol).
enum DDCrypto {
    static let alphabet = Array("0123456789ABCDEFGHJKMNPQRSTVWXYZ")

    static func random(_ count: Int) -> Data {
        var data = Data(count: count)
        _ = data.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, count, $0.baseAddress!) }
        return data
    }

    static func newGroupCode() -> String {
        String(random(20).map { alphabet[Int($0) & 31] })
    }

    static func normalize(code: String) -> String? {
        var out = ""
        for raw in code.uppercased() {
            if raw == "-" || raw == " " { continue }
            var ch = raw
            if ch == "O" { ch = "0" }
            if ch == "I" || ch == "L" { ch = "1" }
            guard alphabet.contains(ch) else { return nil }
            out.append(ch)
        }
        return out.count == 20 ? out : nil
    }

    static func display(code: String) -> String {
        let chars = Array(code)
        return stride(from: 0, to: chars.count, by: 5)
            .map { String(chars[$0..<min($0 + 5, chars.count)]) }
            .joined(separator: "-")
    }

    static func sha256(_ data: Data) -> Data { Data(SHA256.hash(data: data)) }

    static func groupKey(code: String) -> Data { sha256(Data("dropdock/group/v1:\(code)".utf8)) }

    static func groupTag(key: Data) -> String {
        let mac = hmac(key: key, Data("dropdock/tag/v1".utf8))
        return String(mac.map { String(format: "%02x", $0) }.joined().prefix(16))
    }

    static func hmac(key: Data, _ parts: Data...) -> Data {
        var h = HMAC<SHA256>(key: SymmetricKey(data: key))
        for p in parts { h.update(data: p) }
        return Data(h.finalize())
    }

    static func hmacValid(_ tag: Data, key: Data, _ parts: Data...) -> Bool {
        var joined = Data()
        for p in parts { joined.append(p) }
        return HMAC<SHA256>.isValidAuthenticationCode(tag, authenticating: joined, using: SymmetricKey(data: key))
    }

    static func hkdf(ikm: Data, salt: Data, info: String, count: Int) -> Data {
        let key = HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: ikm), salt: salt,
                                         info: Data(info.utf8), outputByteCount: count)
        return key.withUnsafeBytes { Data($0) }
    }

    static func aesCBC(_ op: Int, key: Data, iv: Data, _ input: Data) throws -> Data {
        var out = Data(count: input.count + kCCBlockSizeAES128)
        var moved = 0
        let outCount = out.count
        let status = out.withUnsafeMutableBytes { o in
            input.withUnsafeBytes { i in
                key.withUnsafeBytes { k in
                    iv.withUnsafeBytes { v in
                        CCCrypt(CCOperation(op), CCAlgorithm(kCCAlgorithmAES), CCOptions(kCCOptionPKCS7Padding),
                                k.baseAddress, key.count, v.baseAddress,
                                i.baseAddress, input.count, o.baseAddress, outCount, &moved)
                    }
                }
            }
        }
        guard status == kCCSuccess else { throw CryptoError.cipher }
        out.count = moved
        return out
    }

    /// The six digit check code both sides show while linking.
    static func sas(_ secret: Data) -> String {
        let mac = [UInt8](hmac(key: secret, Data("dropdock/sas".utf8)))
        let v = (UInt32(mac[0]) << 24 | UInt32(mac[1]) << 16 | UInt32(mac[2]) << 8 | UInt32(mac[3])) % 1_000_000
        return String(format: "%03d %03d", v / 1000, v % 1000)
    }

    static func linkSecret(mine: P256.KeyAgreement.PrivateKey, theirs: Data) throws -> Data {
        guard theirs.count == 65, let pub = try? P256.KeyAgreement.PublicKey(x963Representation: theirs) else {
            throw CryptoError.badKey
        }
        let shared = try mine.sharedSecretFromKeyAgreement(with: pub)
        return sha256(shared.withUnsafeBytes { Data($0) })
    }

    static func linkKeys(secret: Data) -> (enc: Data, mac: Data) {
        let okm = hkdf(ikm: secret, salt: Data("dropdock/link-salt".utf8), info: "dropdock/link/v1", count: 64)
        return (Data(okm[0..<32]), Data(okm[32..<64]))
    }
}

func u64be(_ v: UInt64) -> Data {
    withUnsafeBytes(of: v.bigEndian) { Data($0) }
}

/// Encrypts frames in one direction (AES-256-CBC then HMAC-SHA256 over seq, IV and ciphertext).
final class Sealer: @unchecked Sendable {
    private let enc: Data
    private let mac: SymmetricKey
    private var seq: UInt64 = 0

    init(enc: Data, mac: Data) {
        self.enc = enc
        self.mac = SymmetricKey(data: mac)
    }

    func seal(_ plain: Data) throws -> Data {
        let iv = DDCrypto.random(16)
        let ct = try DDCrypto.aesCBC(kCCEncrypt, key: enc, iv: iv, plain)
        var h = HMAC<SHA256>(key: mac)
        h.update(data: u64be(seq))
        h.update(data: iv)
        h.update(data: ct)
        seq += 1
        var box = iv
        box.append(ct)
        box.append(Data(h.finalize()))
        return box
    }
}

final class Opener: @unchecked Sendable {
    private let enc: Data
    private let mac: SymmetricKey
    private var seq: UInt64 = 0

    init(enc: Data, mac: Data) {
        self.enc = enc
        self.mac = SymmetricKey(data: mac)
    }

    func open(_ box: Data) throws -> Data {
        let b = Data(box)
        guard b.count >= 16 + 16 + 32 else { throw CryptoError.badTag }
        let iv = b[0..<16]
        let ct = b[16..<(b.count - 32)]
        let tag = b[(b.count - 32)...]
        var joined = u64be(seq)
        joined.append(iv)
        joined.append(ct)
        guard HMAC<SHA256>.isValidAuthenticationCode(tag, authenticating: joined, using: mac) else {
            throw CryptoError.badTag
        }
        seq += 1
        return try DDCrypto.aesCBC(kCCDecrypt, key: enc, iv: Data(iv), Data(ct))
    }
}
