using System.Numerics;
using System.Security.Cryptography;

namespace MyProxy.Core;

/// <summary>
/// Ed25519 signature verification (RFC 8032), implemented with BigInteger.
/// This verifier does not sign or perform I/O. Client and server implementations use the same RFC test vectors.
/// </summary>
public static class Ed25519
{
    private static readonly BigInteger P =
        BigInteger.Pow(2, 255) - 19;

    /// <summary>群的阶 L。S ≥ L 的签名一律拒绝，避免签名可塑。</summary>
    private static readonly BigInteger L =
        BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");

    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    private static readonly BigInteger SqrtMinusOne = ModPow(2, (P - 1) / 4);

    private static readonly BigInteger[] Identity = { BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero };
    private static readonly BigInteger[] BasePoint = BuildBasePoint();

    /// <summary>
    /// 验证签名。任何畸形输入返回 <c>false</c> 而不是抛异常：调用方分不清
    /// 「公钥编码错」和「签名不对」，也就没法把这个区别泄漏出去。
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64)
        {
            return false;
        }

        BigInteger[]? pointA = Decompress(publicKey);
        if (pointA is null)
        {
            return false;
        }

        ReadOnlySpan<byte> encodedR = signature[..32];
        BigInteger[]? pointR = Decompress(encodedR);
        if (pointR is null)
        {
            return false;
        }

        BigInteger s = LittleEndian(signature[32..]);
        if (s >= L)
        {
            // 非规范 S：它编码的是一个等价标量，做归约而不是拒绝，会让同一个
            // 签名对应两串不同的字节。
            return false;
        }

        byte[] digestInput = new byte[32 + 32 + message.Length];
        encodedR.CopyTo(digestInput);
        publicKey.CopyTo(digestInput.AsSpan(32));
        message.CopyTo(digestInput.AsSpan(64));
        BigInteger k = BigInteger.Remainder(LittleEndian(SHA512.HashData(digestInput)), L);

        BigInteger[] left = Multiply(s, BasePoint);
        BigInteger[] right = Add(pointR, Multiply(k, pointA));
        return PointEquals(left, right);
    }

    private static BigInteger[] BuildBasePoint()
    {
        BigInteger y = Mod(4 * Inverse(5));
        BigInteger x = RecoverX(y, 0) ?? BigInteger.Zero;
        return new[] { x, y, BigInteger.One, Mod(x * y) };
    }

    private static BigInteger Mod(BigInteger value)
    {
        BigInteger result = BigInteger.Remainder(value, P);
        return result.Sign < 0 ? result + P : result;
    }

    private static BigInteger ModPow(BigInteger value, BigInteger exponent) =>
        BigInteger.ModPow(Mod(value), exponent, P);

    private static BigInteger Inverse(BigInteger value) => ModPow(value, P - 2);

    private static BigInteger LittleEndian(ReadOnlySpan<byte> data) =>
        new(data, isUnsigned: true, isBigEndian: false);

    private static BigInteger? RecoverX(BigInteger y, int sign)
    {
        if (y >= P)
        {
            return null;
        }

        BigInteger x2 = Mod((y * y - 1) * Inverse(D * y * y + 1));
        if (x2.IsZero)
        {
            return sign != 0 ? null : BigInteger.Zero;
        }

        BigInteger x = ModPow(x2, (P + 3) / 8);
        if (!Mod(x * x - x2).IsZero)
        {
            x = Mod(x * SqrtMinusOne);
        }

        if (!Mod(x * x - x2).IsZero)
        {
            return null;
        }

        if ((int)(x & BigInteger.One) != sign)
        {
            x = P - x;
        }

        return x;
    }

    private static BigInteger[]? Decompress(ReadOnlySpan<byte> data)
    {
        if (data.Length != 32)
        {
            return null;
        }

        BigInteger value = LittleEndian(data);
        int sign = (int)((value >> 255) & BigInteger.One);
        BigInteger y = value & ((BigInteger.One << 255) - 1);
        BigInteger? x = RecoverX(y, sign);
        return x is null ? null : new[] { x.Value, y, BigInteger.One, Mod(x.Value * y) };
    }

    // 扩展齐次坐标 (X, Y, Z, T)，x = X/Z、y = Y/Z；RFC 8032
    private static BigInteger[] Add(BigInteger[] p, BigInteger[] q)
    {
        BigInteger a = Mod((p[1] - p[0]) * (q[1] - q[0]));
        BigInteger b = Mod((p[1] + p[0]) * (q[1] + q[0]));
        BigInteger c = Mod(2 * p[3] * q[3] * D);
        BigInteger d = Mod(2 * p[2] * q[2]);
        BigInteger e = b - a;
        BigInteger f = d - c;
        BigInteger g = d + c;
        BigInteger h = b + a;
        return new[] { Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h) };
    }

    private static BigInteger[] Multiply(BigInteger scalar, BigInteger[] point)
    {
        BigInteger[] result = Identity;
        BigInteger[] addend = point;
        while (scalar.Sign > 0)
        {
            if (!(scalar & BigInteger.One).IsZero)
            {
                result = Add(result, addend);
            }

            addend = Add(addend, addend);
            scalar >>= 1;
        }

        return result;
    }

    private static bool PointEquals(BigInteger[] p, BigInteger[] q)
    {
        // 齐次坐标不唯一，必须交叉相乘比较，不能逐分量比。
        if (!Mod(p[0] * q[2] - q[0] * p[2]).IsZero)
        {
            return false;
        }

        return Mod(p[1] * q[2] - q[1] * p[2]).IsZero;
    }
}
