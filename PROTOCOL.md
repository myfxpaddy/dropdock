# DropDock wire protocol, version 1

Both the Mac app (Swift) and the Windows app (C#) implement exactly this. Any change here must be
made on both sides.

## Ports

| Purpose   | Transport | Port  |
|-----------|-----------|-------|
| Discovery | UDP       | 47820 |
| Transfers | TCP       | 47821 (falls back to a random port if busy; the real port is advertised) |

## Identity and groups

* `id`: a random lowercase UUID, created on first run and kept forever.
* `name`: the computer name. `os`: `mac` or `windows`. `kind`: `laptop` or `desktop`.
* Group code: 20 characters from the Crockford alphabet `0123456789ABCDEFGHJKMNPQRSTVWXYZ`
  (100 bits). Shown as `XXXXX-XXXXX-XXXXX-XXXXX`. When typed in, it is upper-cased, `-` and spaces
  are removed, `O` becomes `0`, `I` and `L` become `1`.
* Group key `K = SHA256(utf8("dropdock/group/v1:" + code))`.
* Group tag = first 16 lowercase hex characters of `HMAC-SHA256(K, utf8("dropdock/tag/v1"))`.

Computers with the same tag are "linked". Only linked computers can exchange files and clips.
Every install starts in its own group. Linking (below) copies one computer's code to the other.

## Discovery (UDP)

Each packet is one UTF-8 JSON object, under 1200 bytes:

```json
{"app":"dropdock","v":1,"t":"here","id":"...","name":"Office PC","os":"windows",
 "kind":"desktop","port":47821,"tag":"0123456789abcdef","gt":1760000000000}
```

* `t` is `hello`, `here` or `bye`.
* `gt` is the `time` of this computer's General clip, or 0 if it has none.
* Packets are sent to 255.255.255.255 and to the broadcast address of every IPv4 interface that is
  up. Sockets bind 0.0.0.0:47820 with address reuse.
* `hello`: sent at start-up, after wake and after a network change. Everyone who hears a `hello`
  broadcasts a `here` after a random 50 to 400 ms delay (one reply even if several hellos arrive).
* `here`: heartbeat, broadcast every 50 seconds.
* `bye`: sent on quit and before sleep. Receivers drop the peer at once.
* A peer not heard from for 125 seconds is dropped.
* The peer's TCP address is the source IP of its packet, with the advertised `port`.
* Packets with your own `id` are ignored.
* General sync: when you hear a linked peer whose `gt` is older than your General clip's `time`, push
  your General clip to it (once per clip per peer).

## TCP framing

Every frame is `u32 big-endian length` followed by that many bytes. Frames over 48 MiB are refused.

Before authentication, the payload is UTF-8 JSON. After authentication, the payload is an encrypted
box (below).

### Encrypted box

Each direction has its own 32-byte `enc` and `mac` keys and its own frame counter `seq`, starting at
0 and going up by 1 per frame.

```
box = IV (16 random bytes) || CT || TAG (32 bytes)
CT  = AES-256-CBC(enc, IV, PKCS7(plaintext))
TAG = HMAC-SHA256(mac, u64be(seq) || IV || CT)
```

Plaintext is one type byte followed by the body: `0x01` = UTF-8 JSON, `0x02` = raw file bytes.

## Session (linked computers)

1. Client sends `{"t":"hello","app":"dropdock","v":1,"id","name","os","kind","tag","nonce":b64(32 random bytes)}`.
2. If the tag differs, the server answers `{"t":"error","code":"not-linked"}` and closes.
   Otherwise it answers `{"t":"hello","app":"dropdock","v":1,"id","name","os","kind","nonce":b64(sn),"proof":b64(HMAC(K, "dropdock/server" || cn || sn))}`.
3. The client checks the proof, then sends `{"t":"auth","proof":b64(HMAC(K, "dropdock/client" || cn || sn))}`.
4. If the proof is wrong the server sends `{"t":"error","code":"auth"}` and closes.
5. Keys: `okm = HKDF-SHA256(ikm=K, salt=cn||sn, info="dropdock/session/v1", length=128)`.
   Client to server: `enc=okm[0:32]`, `mac=okm[32:64]`. Server to client: `enc=okm[64:96]`, `mac=okm[96:128]`.
6. Server sends encrypted `{"t":"ready"}`. Then the client sends exactly one request.

(`||` means byte concatenation; labels are their UTF-8 bytes.)

### Request: files

```json
{"t":"files","total":123456,"count":2,
 "items":[{"path":"Holiday","size":0,"dir":true},
          {"path":"Holiday/beach.jpg","size":123000,"dir":false},
          {"path":"notes.txt","size":456,"dir":false}]}
```

* Paths always use `/`. No empty, `.` or `..` parts. Receivers clean names their file system cannot
  hold.
* The receiver answers `{"t":"accept"}` or `{"t":"reject","reason":"..."}`.
* The sender then sends every non-directory item in order as `0x02` frames (at most 512 KiB of data
  each). A frame never holds bytes from two files. Empty files have no frames.
* The sender sends `{"t":"end"}`. The receiver moves everything into place and answers
  `{"t":"done","saved":["Holiday","notes.txt"]}` (the final top-level names) or
  `{"t":"error","message":"..."}`.
* Receivers save into `Documents/DropDock`. A name that already exists gets ` (2)`, ` (3)` and so on.

### Request: clip

```json
{"t":"clip","clip":{"id":"uuid","scope":"general","from":"<sender id>","fromName":"MacBook",
 "time":1760000000000,"text":"...","html":"...","rtf":"<base64>","png":"<base64>"}}
```

* `scope` is `general` (shared board) or `direct` (just this computer).
* Every format is optional, but at least one is present. `html` is a normal HTML document or
  fragment (no Windows `CF_HTML` header). `time` is milliseconds since 1970.
* The receiver answers `{"t":"ok"}`.
* `general`: keep it if its `time` is newer than the General clip you have.
* `direct`: keep it as the latest clip from that sender, and put it on the clipboard if that setting is on.

## Linking

1. Client sends `{"t":"link","app":"dropdock","v":1,"id","name","os","kind","pub":b64(P-256 public key, 65-byte uncompressed X9.63)}`.
2. Server answers `{"t":"link-wait","app":"dropdock","v":1,"id","name","os","kind","pub":b64(its own fresh key)}`.
3. Both compute `s = SHA256(ECDH shared x-coordinate)` and the check code
   `sas = u32be(HMAC(s, "dropdock/sas")[0:4]) mod 1000000`, shown as `123 456`.
4. The server asks its user to allow the link (90 second limit).
5. Denied: `{"t":"link-deny"}`. Allowed: `{"t":"link-ok","box":b64(box)}` where the box uses
   `okm = HKDF-SHA256(ikm=s, salt="dropdock/link-salt", info="dropdock/link/v1", length=64)`,
   `enc=okm[0:32]`, `mac=okm[32:64]`, `seq=0`, and the plaintext is `0x01` + `{"code":"<group code>"}`.
6. The client adopts that code, saves it and announces itself with a `hello`.
