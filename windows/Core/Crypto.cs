using System;
using System.Security.Cryptography;
using System.Text;

namespace DropDock.Core
{
    /// <summary>Primitives shared with the Mac build. The wire format is described in README.md (Protocol).</summary>
    public static class DDCrypto
    {
        public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public static byte[] Random(int count)
        {
            var b = new byte[count];
            lock (Rng) Rng.GetBytes(b);
            return b;
        }

        public static string NewGroupCode()
        {
            var bytes = Random(20);
            var sb = new StringBuilder(20);
            foreach (var b in bytes) sb.Append(Alphabet[b & 31]);
            return sb.ToString();
        }

        public static string Normalize(string code)
        {
            if (code == null) return null;
            var sb = new StringBuilder();
            foreach (char raw in code.ToUpperInvariant())
            {
                if (raw == '-' || raw == ' ') continue;
                char c = raw;
                if (c == 'O') c = '0';
                if (c == 'I' || c == 'L') c = '1';
                if (Alphabet.IndexOf(c) < 0) return null;
                sb.Append(c);
            }
            return sb.Length == 20 ? sb.ToString() : null;
        }

        public static string Display(string code)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < code.Length; i += 5)
            {
                if (i > 0) sb.Append('-');
                sb.Append(code.Substring(i, Math.Min(5, code.Length - i)));
            }
            return sb.ToString();
        }

        public static byte[] Utf8(string s) { return Encoding.UTF8.GetBytes(s); }

        public static byte[] Sha256(byte[] data)
        {
            using (var h = SHA256.Create()) return h.ComputeHash(data);
        }

        public static byte[] GroupKey(string code) { return Sha256(Utf8("dropdock/group/v1:" + code)); }

        public static string GroupTag(byte[] key)
        {
            var mac = Hmac(key, Utf8("dropdock/tag/v1"));
            var sb = new StringBuilder();
            for (int i = 0; i < 8; i++) sb.Append(mac[i].ToString("x2"));
            return sb.ToString();
        }

        public static byte[] Hmac(byte[] key, params byte[][] parts)
        {
            using (var h = new HMACSHA256(key))
            {
                foreach (var p in parts) h.TransformBlock(p, 0, p.Length, null, 0);
                h.TransformFinalBlock(new byte[0], 0, 0);
                return h.Hash;
            }
        }

        public static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static byte[] Concat(params byte[][] parts)
        {
            int n = 0;
            foreach (var p in parts) n += p.Length;
            var r = new byte[n];
            int o = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }

        public static byte[] Slice(byte[] a, int start, int count)
        {
            var r = new byte[count];
            Buffer.BlockCopy(a, start, r, 0, count);
            return r;
        }

        /// <summary>RFC 5869 HKDF with SHA-256.</summary>
        public static byte[] Hkdf(byte[] ikm, byte[] salt, string info, int length)
        {
            if (salt == null || salt.Length == 0) salt = new byte[32];
            byte[] prk = Hmac(salt, ikm);
            var infoBytes = Utf8(info);
            var okm = new byte[length];
            var t = new byte[0];
            int done = 0;
            for (byte counter = 1; done < length; counter++)
            {
                t = Hmac(prk, t, infoBytes, new[] { counter });
                int take = Math.Min(t.Length, length - done);
                Buffer.BlockCopy(t, 0, okm, done, take);
                done += take;
            }
            return okm;
        }

        public static byte[] AesCbc(bool encrypt, byte[] key, byte[] iv, byte[] data, int offset, int count)
        {
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.IV = iv;
                using (var t = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor())
                    return t.TransformFinalBlock(data, offset, count);
            }
        }

        public static byte[] U64be(ulong v)
        {
            var b = new byte[8];
            for (int i = 7; i >= 0; i--) { b[i] = (byte)(v & 0xff); v >>= 8; }
            return b;
        }

        /// <summary>The six digit check code both sides show while linking.</summary>
        public static string Sas(byte[] secret)
        {
            var mac = Hmac(secret, Utf8("dropdock/sas"));
            uint v = ((uint)mac[0] << 24 | (uint)mac[1] << 16 | (uint)mac[2] << 8 | mac[3]) % 1000000;
            return (v / 1000).ToString("000") + " " + (v % 1000).ToString("000");
        }

        public static void LinkKeys(byte[] secret, out byte[] enc, out byte[] mac)
        {
            var okm = Hkdf(secret, Utf8("dropdock/link-salt"), "dropdock/link/v1", 64);
            enc = Slice(okm, 0, 32);
            mac = Slice(okm, 32, 32);
        }
    }

    /// <summary>A throwaway P-256 key for linking. Public keys travel as 65-byte uncompressed points.</summary>
    public sealed class LinkKey : IDisposable
    {
        readonly ECDiffieHellman ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        public byte[] PublicKey
        {
            get
            {
                var p = ecdh.ExportParameters(false);
                return DDCrypto.Concat(new byte[] { 4 }, Pad32(p.Q.X), Pad32(p.Q.Y));
            }
        }

        static byte[] Pad32(byte[] v)
        {
            if (v.Length == 32) return v;
            var r = new byte[32];
            Buffer.BlockCopy(v, 0, r, 32 - v.Length, v.Length);
            return r;
        }

        /// <summary>SHA-256 of the shared x-coordinate.</summary>
        public byte[] Secret(byte[] theirs)
        {
            if (theirs == null || theirs.Length != 65 || theirs[0] != 4) throw new CryptographicException("Bad public key");
            var parameters = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = DDCrypto.Slice(theirs, 1, 32), Y = DDCrypto.Slice(theirs, 33, 32) }
            };
            using (var other = ECDiffieHellman.Create(parameters))
                return ecdh.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256);
        }

        public void Dispose() { ecdh.Dispose(); }
    }

    /// <summary>Encrypts frames in one direction: AES-256-CBC, then HMAC-SHA256 over seq, IV and ciphertext.</summary>
    public sealed class Sealer
    {
        readonly byte[] enc, mac;
        ulong seq;

        public Sealer(byte[] enc, byte[] mac) { this.enc = enc; this.mac = mac; }

        public byte[] Seal(byte[] plain) { return Seal(plain, 0, plain.Length); }

        public byte[] Seal(byte[] plain, int offset, int count)
        {
            var iv = DDCrypto.Random(16);
            var ct = DDCrypto.AesCbc(true, enc, iv, plain, offset, count);
            var tag = DDCrypto.Hmac(mac, DDCrypto.U64be(seq), iv, ct);
            seq++;
            return DDCrypto.Concat(iv, ct, tag);
        }
    }

    public sealed class Opener
    {
        readonly byte[] enc, mac;
        ulong seq;

        public Opener(byte[] enc, byte[] mac) { this.enc = enc; this.mac = mac; }

        public byte[] Open(byte[] box)
        {
            if (box.Length < 16 + 16 + 32) throw new CryptographicException("Frame too short");
            var iv = DDCrypto.Slice(box, 0, 16);
            int ctLen = box.Length - 48;
            var ct = DDCrypto.Slice(box, 16, ctLen);
            var tag = DDCrypto.Slice(box, box.Length - 32, 32);
            var expect = DDCrypto.Hmac(mac, DDCrypto.U64be(seq), iv, ct);
            if (!DDCrypto.SameBytes(tag, expect)) throw new CryptographicException("Frame failed authentication");
            seq++;
            return DDCrypto.AesCbc(false, enc, iv, ct, 0, ct.Length);
        }
    }
}
