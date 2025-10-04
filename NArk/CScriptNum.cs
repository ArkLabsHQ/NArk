namespace NArk;

internal class CScriptNum
{
    const long nMaxNumSize = 4;
    /**
			 * Numeric opcodes (OP_1ADD, etc) are restricted to operating on 4-byte integers.
			 * The semantics are subtle, though: operands must be in the range [-2^31 +1...2^31 -1],
			 * but results may overflow (and are valid as long as they are not used in a subsequent
			 * numeric operation). CScriptNum enforces those semantics by storing results as
			 * an int64 and allowing out-of-range values to be returned as a vector of bytes but
			 * throwing an exception if arithmetic is done or the result is interpreted as an integer.
			 */

    public CScriptNum(long n)
    {
        m_value = n;
    }
    private long m_value;

    public CScriptNum(byte[] vch, bool fRequireMinimal)
        : this(vch, fRequireMinimal, 4)
    {

    }
    public CScriptNum(byte[] vch, bool fRequireMinimal, long nMaxNumSize)
    {
        if (vch.Length > nMaxNumSize)
        {
            throw new ArgumentException("script number overflow", "vch");
        }
        if (fRequireMinimal && vch.Length > 0)
        {
            // Check that the number is encoded with the minimum possible
            // number of bytes.
            //
            // If the most-significant-byte - excluding the sign bit - is zero
            // then we're not minimal. Note how this test also rejects the
            // negative-zero encoding, 0x80.
            if ((vch[vch.Length - 1] & 0x7f) == 0)
            {
                // One exception: if there's more than one byte and the most
                // significant bit of the second-most-significant-byte is set
                // it would conflict with the sign bit. An example of this case
                // is +-255, which encode to 0xff00 and 0xff80 respectively.
                // (big-endian).
                if (vch.Length <= 1 || (vch[vch.Length - 2] & 0x80) == 0)
                {
                    throw new ArgumentException("non-minimally encoded script number", "vch");
                }
            }
        }
        m_value = set_vch(vch);
    }

    public override int GetHashCode()
    {
        return getint();
    }
    public override bool Equals(object obj)
    {
        if (obj == null || !(obj is CScriptNum))
            return false;
        CScriptNum item = (CScriptNum)obj;
        return m_value == item.m_value;
    }
    public static bool operator ==(CScriptNum num, long rhs)
    {
        return num.m_value == rhs;
    }
    public static bool operator !=(CScriptNum num, long rhs)
    {
        return num.m_value != rhs;
    }
    public static bool operator <=(CScriptNum num, long rhs)
    {
        return num.m_value <= rhs;
    }
    public static bool operator <(CScriptNum num, long rhs)
    {
        return num.m_value < rhs;
    }
    public static bool operator >=(CScriptNum num, long rhs)
    {
        return num.m_value >= rhs;
    }
    public static bool operator >(CScriptNum num, long rhs)
    {
        return num.m_value > rhs;
    }

    public static bool operator ==(CScriptNum a, CScriptNum b)
    {
        return a.m_value == b.m_value;
    }
    public static bool operator !=(CScriptNum a, CScriptNum b)
    {
        return a.m_value != b.m_value;
    }
    public static bool operator <=(CScriptNum a, CScriptNum b)
    {
        return a.m_value <= b.m_value;
    }
    public static bool operator <(CScriptNum a, CScriptNum b)
    {
        return a.m_value < b.m_value;
    }
    public static bool operator >=(CScriptNum a, CScriptNum b)
    {
        return a.m_value >= b.m_value;
    }
    public static bool operator >(CScriptNum a, CScriptNum b)
    {
        return a.m_value > b.m_value;
    }

    public static CScriptNum operator +(CScriptNum num, long rhs)
    {
        return new CScriptNum(num.m_value + rhs);
    }
    public static CScriptNum operator -(CScriptNum num, long rhs)
    {
        return new CScriptNum(num.m_value - rhs);
    }
    public static CScriptNum operator +(CScriptNum a, CScriptNum b)
    {
        return new CScriptNum(a.m_value + b.m_value);
    }
    public static CScriptNum operator -(CScriptNum a, CScriptNum b)
    {
        return new CScriptNum(a.m_value - b.m_value);
    }

    public static CScriptNum operator &(CScriptNum a, long b)
    {
        return new CScriptNum(a.m_value & b);
    }
    public static CScriptNum operator &(CScriptNum a, CScriptNum b)
    {
        return new CScriptNum(a.m_value & b.m_value);
    }



    public static CScriptNum operator -(CScriptNum num)
    {
        assert(num.m_value != Int64.MinValue);
        return new CScriptNum(-num.m_value);
    }

    private static void assert(bool result)
    {
        if (!result)
            throw new InvalidOperationException("Assertion fail for CScriptNum");
    }

    public static implicit operator CScriptNum(long rhs)
    {
        return new CScriptNum(rhs);
    }

    public static explicit operator long(CScriptNum rhs)
    {
        return rhs.m_value;
    }

    public static explicit operator uint(CScriptNum rhs)
    {
        return (uint)rhs.m_value;
    }



    public int getint()
    {
        if (m_value > int.MaxValue)
            return int.MaxValue;
        else if (m_value < int.MinValue)
            return int.MinValue;
        return (int)m_value;
    }

    public byte[] getvch()
    {
        return serialize(m_value);
    }

    internal static byte[] serialize(long value)
    {
        if (value == 0)
            return new byte[0];

        var result = new List<byte>(8);
        bool neg = value < 0;
        long absvalue = neg ? -value : value;

        while (absvalue != 0)
        {
            result.Add((byte)(absvalue & 0xff));
            absvalue >>= 8;
        }

        //    - If the most significant byte is >= 0x80 and the value is positive, push a
        //    new zero-byte to make the significant byte < 0x80 again.

        //    - If the most significant byte is >= 0x80 and the value is negative, push a
        //    new 0x80 byte that will be popped off when converting to an integral.

        //    - If the most significant byte is < 0x80 and the value is negative, add
        //    0x80 to it, since it will be subtracted and interpreted as a negative when
        //    converting to an integral.

        if ((result[result.Count - 1] & 0x80) != 0)
            result.Add((byte)(neg ? 0x80 : 0));
        else if (neg)
            result[result.Count - 1] |= 0x80;

        return result.ToArray();
    }

    static long set_vch(byte[] vch)
    {
        if (vch.Length == 0)
            return 0;

        long result = 0;
        for (int i = 0; i != vch.Length; ++i)
            result |= ((long)(vch[i])) << 8 * i;

        // If the input vector's most significant byte is 0x80, remove it from
        // the result's msb and return a negative.
        if ((vch[vch.Length - 1] & 0x80) != 0)
        {
            var temp = ~(0x80UL << (8 * (vch.Length - 1)));
            return -((long)((ulong)result & temp));
        }

        return result;
    }
}