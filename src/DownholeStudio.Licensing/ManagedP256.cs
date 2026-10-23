using System.Numerics;
using System.Security.Cryptography;

namespace DownholeStudio.Licensing;

/// <summary>
/// ECDSA P-256 / SHA-256 signature verification in managed code (FIPS 186-4, curve P-256). Used when the operating
/// system's cryptography provider cannot verify, for example a restricted or incomplete provider on a customer's PC
/// or a compatibility layer such as Wine. Verification only processes public data, so it needs no constant-time code.
/// </summary>
public static class ManagedP256
{
    private static readonly BigInteger P = Hex("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF");
    private static readonly BigInteger N = Hex("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");
    private static readonly BigInteger B = Hex("5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B");
    private static readonly Point G = new(Hex("6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296"), Hex("4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5"));

    /// <summary>DER prefix of a SubjectPublicKeyInfo for an uncompressed P-256 public key (id-ecPublicKey, prime256v1).</summary>
    private static readonly byte[] SpkiPrefix = Convert.FromHexString("3059301306072A8648CE3D020106082A8648CE3D030107034200");

    private readonly record struct Point(BigInteger X, BigInteger Y)
    {
        public static readonly Point Infinity = new(-1, -1);

        public bool IsInfinity => X.Sign < 0;
    }

    /// <summary>Verifies an IEEE P1363 (r‖s) signature over <paramref name="data"/> with a PEM SubjectPublicKeyInfo key.</summary>
    public static bool Verify(string publicKeyPem, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 64 || !TryReadPublicKey(publicKeyPem, out var q))
        {
            return false;
        }

        var r = new BigInteger(signature[..32], isUnsigned: true, isBigEndian: true);
        var s = new BigInteger(signature[32..], isUnsigned: true, isBigEndian: true);
        if (r.Sign <= 0 || r >= N || s.Sign <= 0 || s >= N)
        {
            return false;
        }

        var e = new BigInteger(SHA256.HashData(data), isUnsigned: true, isBigEndian: true);
        var w = BigInteger.ModPow(s, N - 2, N);
        var u1 = e * w % N;
        var u2 = r * w % N;
        var x = Add(Multiply(G, u1), Multiply(q, u2));
        return !x.IsInfinity && x.X % N == r;
    }

    private static bool TryReadPublicKey(string pem, out Point q)
    {
        q = Point.Infinity;
        var body = string.Concat(pem.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)));
        byte[] der;
        try
        {
            der = Convert.FromBase64String(body);
        }
        catch (FormatException)
        {
            return false;
        }

        if (der.Length != SpkiPrefix.Length + 65 || !der.AsSpan(0, SpkiPrefix.Length).SequenceEqual(SpkiPrefix) || der[SpkiPrefix.Length] != 0x04)
        {
            return false;
        }

        var x = new BigInteger(der.AsSpan(SpkiPrefix.Length + 1, 32), isUnsigned: true, isBigEndian: true);
        var y = new BigInteger(der.AsSpan(SpkiPrefix.Length + 33, 32), isUnsigned: true, isBigEndian: true);
        if (x >= P || y >= P || Mod(y * y - (x * x * x - 3 * x + B)) != 0)
        {
            return false; // not a point on the curve
        }

        q = new Point(x, y);
        return true;
    }

    private static Point Multiply(Point point, BigInteger k)
    {
        var result = Point.Infinity;
        var addend = point;
        while (k.Sign > 0)
        {
            if (!k.IsEven)
            {
                result = Add(result, addend);
            }

            addend = Add(addend, addend);
            k >>= 1;
        }

        return result;
    }

    private static Point Add(Point a, Point b)
    {
        if (a.IsInfinity)
        {
            return b;
        }

        if (b.IsInfinity)
        {
            return a;
        }

        BigInteger lambda;
        if (a.X == b.X)
        {
            if (Mod(a.Y + b.Y) == 0)
            {
                return Point.Infinity;
            }

            lambda = Mod((3 * a.X * a.X - 3) * Inverse(2 * a.Y));
        }
        else
        {
            lambda = Mod((b.Y - a.Y) * Inverse(b.X - a.X));
        }

        var x = Mod(lambda * lambda - a.X - b.X);
        return new Point(x, Mod(lambda * (a.X - x) - a.Y));
    }

    private static BigInteger Inverse(BigInteger v) => BigInteger.ModPow(Mod(v), P - 2, P);

    private static BigInteger Mod(BigInteger v)
    {
        var m = v % P;
        return m.Sign < 0 ? m + P : m;
    }

    private static BigInteger Hex(string hex) => new(Convert.FromHexString(hex), isUnsigned: true, isBigEndian: true);
}
