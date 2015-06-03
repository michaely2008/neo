﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace AntShares.Cryptography
{
    public class Secp256r1Point
    {
        private readonly Secp256r1Element x, y;

        public bool IsInfinity
        {
            get { return x == null && y == null; }
        }

        internal Secp256r1Point(Secp256r1Element x, Secp256r1Element y)
        {
            if ((x != null && y == null) || (x == null && y != null))
                throw new ArgumentException("Exactly one of the field elements is null");
            this.x = x;
            this.y = y;
        }

        public static Secp256r1Point DecodePoint(byte[] encoded)
        {
            Secp256r1Point p = null;
            int expectedLength = (Secp256r1Curve.Q.GetBitLength() + 7) / 8;
            switch (encoded[0])
            {
                case 0x00: // infinity
                    {
                        if (encoded.Length != 1)
                            throw new ArgumentException("Incorrect length for infinity encoding", "encoded");
                        p = Secp256r1Curve.Infinity;
                        break;
                    }
                case 0x02: // compressed
                case 0x03: // compressed
                    {
                        if (encoded.Length != (expectedLength + 1))
                            throw new ArgumentException("Incorrect length for compressed encoding", "encoded");
                        int yTilde = encoded[0] & 1;
                        BigInteger X1 = new BigInteger(encoded.Skip(1).Reverse().Concat(new byte[1]).ToArray());
                        p = DecompressPoint(yTilde, X1);
                        break;
                    }
                case 0x04: // uncompressed
                case 0x06: // hybrid
                case 0x07: // hybrid
                    {
                        if (encoded.Length != (2 * expectedLength + 1))
                            throw new ArgumentException("Incorrect length for uncompressed/hybrid encoding", "encoded");
                        BigInteger X1 = new BigInteger(encoded.Skip(1).Take(expectedLength).Reverse().Concat(new byte[1]).ToArray());
                        BigInteger Y1 = new BigInteger(encoded.Skip(1 + expectedLength).Reverse().Concat(new byte[1]).ToArray());
                        p = new Secp256r1Point(new Secp256r1Element(X1), new Secp256r1Element(Y1));
                        break;
                    }
                default:
                    throw new FormatException("Invalid point encoding " + encoded[0]);
            }
            return p;
        }

        private static Secp256r1Point DecompressPoint(int yTilde, BigInteger X1)
        {
            Secp256r1Element x = new Secp256r1Element(X1);
            Secp256r1Element alpha = x * (x.Square() + Secp256r1Curve.A) + Secp256r1Curve.B;
            Secp256r1Element beta = alpha.Sqrt();

            //
            // if we can't find a sqrt we haven't got a point on the
            // curve - run!
            //
            if (beta == null)
                throw new ArithmeticException("Invalid point compression");

            BigInteger betaValue = beta.Value;
            int bit0 = betaValue.IsEven ? 0 : 1;

            if (bit0 != yTilde)
            {
                // Use the other root
                beta = new Secp256r1Element(Secp256r1Curve.Q - betaValue);
            }

            return new Secp256r1Point(x, beta);
        }

        public byte[] EncodePoint(bool commpressed)
        {
            byte[] data;
            if (commpressed)
            {
                data = new byte[33];
            }
            else
            {
                data = new byte[65];
                byte[] yBytes = y.Value.ToByteArray().Reverse().ToArray();
                Buffer.BlockCopy(yBytes, 0, data, 65 - yBytes.Length, yBytes.Length);
            }
            byte[] xBytes = x.Value.ToByteArray().Reverse().ToArray();
            Buffer.BlockCopy(xBytes, 0, data, 33 - xBytes.Length, xBytes.Length);
            data[0] = commpressed ? y.Value.IsEven ? (byte)0x02 : (byte)0x03 : (byte)0x04;
            return data;
        }

        public byte[] GetXComponent()
        {
            return x.ToByteArray();
        }

        public byte[] GetYComponent()
        {
            return y.ToByteArray();
        }

        private static Secp256r1Point Multiply(Secp256r1Point p, BigInteger k)
        {
            // floor(log2(k))
            int m = k.GetBitLength();

            // width of the Window NAF
            sbyte width;

            // Required length of precomputation array
            int reqPreCompLen;

            // Determine optimal width and corresponding length of precomputation
            // array based on literature values
            if (m < 13)
            {
                width = 2;
                reqPreCompLen = 1;
            }
            else if (m < 41)
            {
                width = 3;
                reqPreCompLen = 2;
            }
            else if (m < 121)
            {
                width = 4;
                reqPreCompLen = 4;
            }
            else if (m < 337)
            {
                width = 5;
                reqPreCompLen = 8;
            }
            else if (m < 897)
            {
                width = 6;
                reqPreCompLen = 16;
            }
            else if (m < 2305)
            {
                width = 7;
                reqPreCompLen = 32;
            }
            else
            {
                width = 8;
                reqPreCompLen = 127;
            }

            // The length of the precomputation array
            int preCompLen = 1;

            Secp256r1Point[] preComp = preComp = new Secp256r1Point[] { p };
            Secp256r1Point twiceP = p.Twice();

            if (preCompLen < reqPreCompLen)
            {
                // Precomputation array must be made bigger, copy existing preComp
                // array into the larger new preComp array
                Secp256r1Point[] oldPreComp = preComp;
                preComp = new Secp256r1Point[reqPreCompLen];
                Array.Copy(oldPreComp, 0, preComp, 0, preCompLen);

                for (int i = preCompLen; i < reqPreCompLen; i++)
                {
                    // Compute the new ECPoints for the precomputation array.
                    // The values 1, 3, 5, ..., 2^(width-1)-1 times p are
                    // computed
                    preComp[i] = twiceP + preComp[i - 1];
                }
            }

            // Compute the Window NAF of the desired width
            sbyte[] wnaf = WindowNaf(width, k);
            int l = wnaf.Length;

            // Apply the Window NAF to p using the precomputed ECPoint values.
            Secp256r1Point q = Secp256r1Curve.Infinity;
            for (int i = l - 1; i >= 0; i--)
            {
                q = q.Twice();

                if (wnaf[i] != 0)
                {
                    if (wnaf[i] > 0)
                    {
                        q += preComp[(wnaf[i] - 1) / 2];
                    }
                    else
                    {
                        // wnaf[i] < 0
                        q -= preComp[(-wnaf[i] - 1) / 2];
                    }
                }
            }

            return q;
        }

        private Secp256r1Point Twice()
        {
            if (this.IsInfinity)
                return this;
            if (this.y.Value.Sign == 0)
                return Secp256r1Curve.Infinity;
            Secp256r1Element TWO = new Secp256r1Element(2);
            Secp256r1Element THREE = new Secp256r1Element(3);
            Secp256r1Element gamma = (this.x.Square() * THREE + Secp256r1Curve.A) / (y * TWO);
            Secp256r1Element x3 = gamma.Square() - this.x * TWO;
            Secp256r1Element y3 = gamma * (this.x - x3) - this.y;
            return new Secp256r1Point(x3, y3);
        }

        private static sbyte[] WindowNaf(sbyte width, BigInteger k)
        {
            sbyte[] wnaf = new sbyte[k.GetBitLength() + 1];
            short pow2wB = (short)(1 << width);
            int i = 0;
            int length = 0;
            while (k.Sign > 0)
            {
                if (!k.IsEven)
                {
                    BigInteger remainder = k % pow2wB;
                    if (remainder.TestBit(width - 1))
                    {
                        wnaf[i] = (sbyte)(remainder - pow2wB);
                    }
                    else
                    {
                        wnaf[i] = (sbyte)remainder;
                    }
                    k -= wnaf[i];
                    length = i;
                }
                else
                {
                    wnaf[i] = 0;
                }
                k >>= 1;
                i++;
            }
            length++;
            sbyte[] wnafShort = new sbyte[length];
            Array.Copy(wnaf, 0, wnafShort, 0, length);
            return wnafShort;
        }

        public static Secp256r1Point operator -(Secp256r1Point x)
        {
            return new Secp256r1Point(x.x, -x.y);
        }

        public static Secp256r1Point operator *(Secp256r1Point p, byte[] n)
        {
            if (p == null || n == null)
                throw new ArgumentNullException();
            if (n.Length != 32)
                throw new ArgumentException();
            if (p.IsInfinity)
                return p;
            //BigInteger的内存无法被保护，可能会有安全隐患。此处的k需要重写一个SecureBigInteger类来代替
            BigInteger k = new BigInteger(n.Reverse().Concat(new byte[1]).ToArray());
            if (k.Sign == 0)
                return Secp256r1Curve.Infinity;
            return Multiply(p, k);
        }

        public static Secp256r1Point operator +(Secp256r1Point x, Secp256r1Point y)
        {
            if (x.IsInfinity)
                return y;
            if (y.IsInfinity)
                return x;
            if (x.x.Equals(y.x))
            {
                if (x.y.Equals(y.y))
                    return x.Twice();
                Debug.Assert(x.y.Equals(-y.y));
                return Secp256r1Curve.Infinity;
            }
            Secp256r1Element gamma = (y.y - x.y) / (y.x - x.x);
            Secp256r1Element x3 = gamma.Square() - x.x - y.x;
            Secp256r1Element y3 = gamma * (x.x - x3) - x.y;
            return new Secp256r1Point(x3, y3);
        }

        public static Secp256r1Point operator -(Secp256r1Point x, Secp256r1Point y)
        {
            if (y.IsInfinity)
                return x;
            return x + (-y);
        }
    }
}