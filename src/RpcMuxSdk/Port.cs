namespace RpcMuxSdk
{
    using System;
    using System.Diagnostics.CodeAnalysis;

    using BufferKit;

    public readonly struct Port : IComparable<Port>, IEquatable<Port>
    {
        public readonly UInt32 code;

        public Port(UInt32 code)
            => this.code = code;

        public static readonly Port Unspecified = new Port(0);

        public static implicit operator uint(Port port)
            => port.code;

        public static implicit operator Port(UInt32 u32port)
            => new Port(u32port);

        public static implicit operator Port(UInt16 u16port)
            => new Port(u16port);

        public static explicit operator Port(int i32port)
        {
            if (i32port < 0)
                throw new ArgumentOutOfRangeException();
            return new Port((UInt32)i32port);
        }

        public int CompareTo(Port other)
            => this.code.CompareTo(other.code);

        public Result<UInt16, UInt32> GetMinRepr()
        {
            if (this.code <= UInt16.MaxValue)
                return Result.Ok((UInt16)this.code);
            else
                return Result.Err(this.code);
        }

        public bool Equals(Port other)
            => this.code == other.code;

        public override string ToString()
            => $"Port({this.code})";

        public override int GetHashCode()
            => HashCode.Combine(typeof(Port), this.code);

        public override bool Equals([NotNullWhen(true)] object? obj)
            => obj is Port port && port.code == this.code;

        public static bool operator ==(Port lhs, Port rhs)
            => lhs.code == rhs.code;

        public static bool operator !=(Port lhs, Port rhs)
            => lhs.code != rhs.code;
    }
}