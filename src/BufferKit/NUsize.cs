// ReSharper disable BuiltInTypeReferenceStyle
namespace NsBufferKit
{
    using System;
    using System.Diagnostics.CodeAnalysis;

    using NsAnyLR;

    /// <summary>
    /// Unsigned integer types wrapping around nuint
    /// </summary>
    public readonly struct NUsize : IComparable<NUsize>, IEquatable<NUsize>
    {
        public readonly nuint Val;

        public static readonly NUsize Zero = new NUsize(0);

        public static readonly NUsize MinValue = NUsize.Zero;

        public static NUsize MaxValue
        {
            get
            {
                unsafe
                {
                    if (sizeof(nuint) == sizeof(UInt64))
                        return new NUsize(unchecked((nuint)UInt64.MaxValue));
                    if (sizeof(nuint) == sizeof(UInt32))
                        return new NUsize((nuint)UInt32.MaxValue);
                    throw new NotSupportedException("Unsupported NUsize");
                }
            }
        }

        public NUsize(nuint val)
            => this.Val = val;

        #region Convert

        public static implicit operator NUsize(nuint u)
            => new NUsize(u);

        public static implicit operator NUsize(UInt32 u)
            => new NUsize(u);

        public static implicit operator NUsize(UInt16 u)
            => new NUsize(u);

        public static implicit operator NUsize(Byte u)
            => new NUsize(u);

        public static explicit operator NUsize(int i)
        {
            if (i < 0)
                throw new InvalidCastException($"Failed to parse negative value ({i}) to NUsize");
            else
                return new NUsize((uint)i);
        }

        public static explicit operator int(NUsize s)
        {
            if (s.Val > int.MaxValue)
                throw new InvalidCastException($"Failed to parse overflow value ({s.Val}) to int");
            else
                return (int)s.Val;
        }

        public static implicit operator nuint(NUsize s)
            => s.Val;

        public static implicit operator ulong(NUsize s)
            => s.Val;

        public static explicit operator uint(NUsize s)
            => (uint)s.Val;

        public static explicit operator ushort(NUsize s)
            => (ushort)s.Val;

        public static explicit operator byte(NUsize s)
            => (byte)s.Val;

        public static NUsize Min(NUsize a, NUsize b)
            => a > b ? b : a;

        public static NUsize Max(NUsize a, NUsize b)
            => a > b ? a : b;

        public static Result<NUsize, UInt64> TryFrom(UInt64 u)
        {
            unsafe
            {
                if (sizeof(nuint) == sizeof(UInt32) && u > UInt32.MaxValue)
                    return Result.Err(u);
                else
                    return Result.Ok(new NUsize((nuint)u));
            }
        }

        public static Result<NUsize, Int64> TryFrom(Int64 u)
        {
            unsafe
            {
                if (u < 0)
                    return Result.Err(u);
                if (sizeof(nuint) == sizeof(UInt32) && u > UInt32.MaxValue)
                    return Result.Err(u);
                else
                    return Result.Ok(new NUsize((nuint)u));
            }
        }

        public static Result<NUsize, Int32> TryFrom(Int32 u)
            => u < 0 ? Result.Err(u) : Result.Ok(new NUsize((nuint)u));

        public static Result<NUsize, Int16> TryFrom(Int16 u)
            => u < 0 ? Result.Err(u) : Result.Ok(new NUsize((nuint)u));

        public static Result<NUsize, SByte> TryFrom(SByte u)
            => u < 0 ? Result.Err(u) : Result.Ok(new NUsize((nuint)u));

        public bool TryInto(out UInt32 i)
        {
            if (this.Val > UInt32.MaxValue)
            {
                i = default;
                return false;
            }
            else
            {
                i = (UInt32)this.Val;
                return true;
            }
        }

        public bool TryInto(out UInt16 i)
        {
            if (this.Val > UInt16.MaxValue)
            {
                i = default;
                return false;
            }
            else
            {
                i = (UInt16)this.Val;
                return true;
            }
        }

        public bool TryInto(out Byte i)
        {
            if (this.Val > Byte.MaxValue)
            {
                i = default;
                return false;
            }
            else
            {
                i = (Byte)this.Val;
                return true;
            }
        }

        public bool TryInto(out Int64 i)
        {
            unsafe
            {
                if (sizeof(nuint) == sizeof(Int64) && this.Val > Int64.MaxValue)
                {
                    i = default;
                    return false;
                }
                if (sizeof(nuint) == sizeof(Int32))
                {
                    i = (Int64)this.Val;
                    return true;
                }
                throw new NotSupportedException($"Unsupported sizeof(nuint) == {sizeof(nuint)}");
            }
        }

        public bool TryInto(out Int32 i)
        {
            if (this.Val > Int32.MaxValue)
            {
                i = default;
                return false;
            }
            else
            {
                i = (Int32)this.Val;
                return true;
            }
        }

        public bool TryInto(out Int16 i)
        {
            if (this.Val > (UInt32)Int16.MaxValue)
            {
                i = default;
                return false;
            }
            else
            {
                i = (Int16)this.Val;
                return true;
            }
        }

        public bool TryInto(out SByte i)
        {
            if (this.Val > (UInt16)SByte.MaxValue)
            {
                i = default;
                return false;
            }
            else
            {
                i = (SByte)this.Val;
                return true;
            }
        }

        #endregion

        #region Add, Sub, Mul, Div, Mod

        public static NUsize operator +(NUsize a, NUsize b)
            => new NUsize(a.Val + b.Val);

        public static NUsize operator -(NUsize a, NUsize b)
            => new NUsize(a.Val - b.Val);

        public static NUsize operator *(NUsize a, NUsize b)
            => new NUsize(a.Val * b.Val);

        public static NUsize operator /(NUsize a, NUsize b)
            => new NUsize(a.Val / b.Val);

        public static NUsize operator %(NUsize a, NUsize b)
            => new NUsize(a.Val % b.Val);

        public static NUsize operator <<(NUsize a, int b)
            => new NUsize(a.Val << b);

        public static NUsize operator >>(NUsize a, int b)
            => new NUsize(a.Val >> b);

        #endregion

        #region Compare with Int64

        public static bool operator >(NUsize a, Int64 b)
            => b < 0 || a > (UInt64)b;

        public static bool operator <(NUsize a, Int64 b)
            => b >= 0 && a < (UInt64)b;

        public static bool operator >=(NUsize a, Int64 b)
            => b < 0 || a >= (UInt64)b;

        public static bool operator <=(NUsize a, Int64 b)
            => b >= 0 && a <= (UInt64)b;

        public static bool operator ==(NUsize a, Int64 b)
            => b >= 0 && a == (UInt64)b;

        public static bool operator !=(NUsize a, Int64 b)
            => b < 0 || a != (UInt64)b;

        #endregion

        #region Compare with Int32

        public static bool operator >(NUsize a, Int32 b)
            => a > (Int64)b;

        public static bool operator <(NUsize a, Int32 b)
            => a < (Int64)b;

        public static bool operator >=(NUsize a, Int32 b)
            => a >= (Int64)b;

        public static bool operator <=(NUsize a, Int32 b)
            => a <= (Int64)b;

        public static bool operator ==(NUsize a, Int32 b)
            => a == (Int64)b;

        public static bool operator !=(NUsize a, Int32 b)
            => a != (Int64)b;

        #endregion

        #region Compare with Int16

        public static bool operator >(NUsize a, Int16 b)
            => a > (Int64)b;

        public static bool operator <(NUsize a, Int16 b)
            => a < (Int64)b;

        public static bool operator >=(NUsize a, Int16 b)
            => a >= (Int64)b;

        public static bool operator <=(NUsize a, Int16 b)
            => a <= (Int64)b;

        public static bool operator ==(NUsize a, Int16 b)
            => a == (Int64)b;

        public static bool operator !=(NUsize a, Int16 b)
            => a != (Int64)b;

        #endregion

        #region Compare with SByte

        public static bool operator >(NUsize a, SByte b)
            => a > (Int64)b;

        public static bool operator <(NUsize a, SByte b)
            => a < (Int64)b;

        public static bool operator >=(NUsize a, SByte b)
            => a >= (Int64)b;

        public static bool operator <=(NUsize a, SByte b)
            => a <= (Int64)b;

        public static bool operator ==(NUsize a, SByte b)
            => a == (Int64)b;

        public static bool operator !=(NUsize a, SByte b)
            => a != (Int64)b;

        #endregion

        #region Compare with UInt64

        public static bool operator >(NUsize a, UInt64 b)
            => a.Val > b;

        public static bool operator <(NUsize a, UInt64 b)
            => a.Val < b;

        public static bool operator >=(NUsize a, UInt64 b)
            => a.Val >= b;

        public static bool operator <=(NUsize a, UInt64 b)
            => a.Val <= b;

        public static bool operator ==(NUsize a, UInt64 b)
            => a.Val == b;

        public static bool operator !=(NUsize a, UInt64 b)
            => a.Val != b;

        #endregion

        #region Compare with UInt32

        public static bool operator >(NUsize a, UInt32 b)
            => a.Val > b;

        public static bool operator <(NUsize a, UInt32 b)
            => a.Val < b;

        public static bool operator >=(NUsize a, UInt32 b)
            => a.Val >= b;

        public static bool operator <=(NUsize a, UInt32 b)
            => a.Val <= b;

        public static bool operator ==(NUsize a, UInt32 b)
            => a.Val == b;

        public static bool operator !=(NUsize a, UInt32 b)
            => a.Val != b;

        #endregion

        #region Compare with UInt16

        public static bool operator >(NUsize a, UInt16 b)
            => a.Val > b;

        public static bool operator <(NUsize a, UInt16 b)
            => a.Val < b;

        public static bool operator >=(NUsize a, UInt16 b)
            => a.Val >= b;

        public static bool operator <=(NUsize a, UInt16 b)
            => a.Val <= b;

        public static bool operator ==(NUsize a, UInt16 b)
            => a.Val == b;

        public static bool operator !=(NUsize a, UInt16 b)
            => a.Val != b;

        #endregion

        #region Compare with Byte

        public static bool operator >(NUsize a, Byte b)
            => a.Val > b;

        public static bool operator <(NUsize a, Byte b)
            => a.Val < b;

        public static bool operator >=(NUsize a, Byte b)
            => a.Val >= b;

        public static bool operator <=(NUsize a, Byte b)
            => a.Val <= b;

        public static bool operator ==(NUsize a, Byte b)
            => a.Val == b;

        public static bool operator !=(NUsize a, Byte b)
            => a.Val != b;

        #endregion

        #region Comparable

        public int CompareTo(NUsize other)
            => this.Val > other.Val ? 1 : -1;

        public static bool operator >(NUsize a, NUsize b)
            => a.Val > b.Val;

        public static bool operator <(NUsize a, NUsize b)
            => a.Val < b.Val;

        public static bool operator >=(NUsize a, NUsize b)
            => a.Val >= b.Val;

        public static bool operator <=(NUsize a, NUsize b)
            => a.Val <= b.Val;

        #endregion

        #region Equatable

        public static bool operator ==(NUsize a, NUsize b)
            => a.Val == b.Val;

        public static bool operator !=(NUsize a, NUsize b)
            => a.Val != b.Val;

        public bool Equals(NUsize other)
            => this.Val == other.Val;

        public override bool Equals([NotNullWhen(true)] object? obj)
            => obj is NUsize other && this.Equals(other);

        public override int GetHashCode()
            => this.Val.GetHashCode();

        public override string ToString()
            => this.Val.ToString();

        #endregion
    }
}
