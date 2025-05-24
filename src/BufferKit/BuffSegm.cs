namespace NsBufferKit
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis; // to use [NotNullWhen()] for override object.Equals

    #region internal IBuffSegm variants

    internal interface IBuffSegm<T>
    {
        public NUsize Length { get; }
    }

    internal interface IReaderBuffSegm<T>: IBuffSegm<T>
    {
        public void Forward(NUsize length);
    }

    internal interface IWriterBuffSegm<T>: IBuffSegm<T>
    {
        public void Forward(NUsize length);
    }

    #endregion

    public readonly struct BuffSegmError : IIoError
    {
        private readonly uint code_;

        private BuffSegmError(uint code)
            => this.code_ = code;

        public static readonly ReadOnlyMemory<string> NAMES = new(new string[] { nameof(Borrowed), nameof(Disposed), nameof(Insufficient) });

        public static readonly BuffSegmError Borrowed = new(0);

        public static readonly BuffSegmError Disposed = new(1);

        public static readonly BuffSegmError Insufficient = new(2);

        public Exception AsException()
            => new Exception(this.ToString());

        public override string ToString()
            => $"{nameof(BuffSegmError)}.{NAMES.Span[(int)this.code_]}";

        public static bool operator ==(BuffSegmError lhs, BuffSegmError rhs)
            => lhs.code_ == rhs.code_;

        public static bool operator !=(BuffSegmError rhs, BuffSegmError lhs)
            => lhs.code_ != rhs.code_;

        public override int GetHashCode()
            => HashCode.Combine(typeof(BuffSegmError), this.code_);

        public override bool Equals([NotNullWhen(true)] object? obj)
            => obj is BuffSegmError err && this.code_ == err.code_;
    }

    public static class MemorySliceNUsizeExtensions
    {
        public static NUsize NUsizeLength<T>(in this Memory<T> memory)
            => (NUsize)memory.Length;

        public static NUsize NUsizeLength<T>(in this ReadOnlyMemory<T> memory)
            => (NUsize)memory.Length;

        public static Memory<T> Slice<T>(in this Memory<T> memory, NUsize offset, NUsize length)
        {
            if (!offset.TryInto(out int offsetI32))
                throw new ArgumentOutOfRangeException(nameof(offset), message: $"offset({offset})");
            if (!length.TryInto(out int lengthI32))
                throw new ArgumentOutOfRangeException(nameof(length), message: $"length({length})");
            return memory.Slice(offsetI32, lengthI32);
        }

        public static Memory<T> Slice<T>(in this Memory<T> memory, NUsize offset)
        {
            if (!offset.TryInto(out int offsetI32))
                throw new ArgumentOutOfRangeException(nameof(offset));
            return memory.Slice(offsetI32);
        }

        public static ReadOnlyMemory<T> Slice<T>(in this ReadOnlyMemory<T> memory, NUsize offset, NUsize length)
        {
            if (!offset.TryInto(out int offsetI32))
                throw new ArgumentOutOfRangeException(nameof(offset), message: $"offset({offset})");
            if (!length.TryInto(out int lengthI32))
                throw new ArgumentOutOfRangeException(nameof(length), message: $"length({length})");
            return memory.Slice(offsetI32, lengthI32);
        }

        public static ReadOnlyMemory<T> Slice<T>(in this ReadOnlyMemory<T> memory, NUsize offset)
        {
            if (!offset.TryInto(out int offsetI32))
                throw new ArgumentOutOfRangeException(nameof(offset));
            return memory.Slice(offsetI32);
        }

        public static Memory<T> Slice<T>(this T[] memory, NUsize offset, NUsize length)
        {
            var rm = new Memory<T>(memory);
            return rm.Slice(offset, length);
        }

        public static Memory<T> Slice<T>(this T[] memory, NUsize offset)
        {
            var rm = new Memory<T>(memory);
            return rm.Slice(offset);
        }

        public static ReadOnlyMemory<T> ReadOnlySlice<T>(this T[] memory, NUsize offset, NUsize length)
        {
            var rm = new ReadOnlyMemory<T>(memory);
            return rm.Slice(offset, length);
        }

        public static ReadOnlyMemory<T> ReadOnlySlice<T>(this T[] memory, NUsize offset)
        {
            var rm = new ReadOnlyMemory<T>(memory);
            return rm.Slice(offset);
        }
    }

    public static class BuffSegmThrowExtension
    {
        internal static Exception UnexpectedTypeException<T>(this IBuffSegm<T> segm)
            => new Exception($"Unexpected source type({segm.GetType()})");
    }

    public static class MemoryEnumerateSpanExtensions
    {
        public static IEnumerable<T> EnumerateSpan<T>(this Memory<T> memory)
        {
            for (var i = 0; i < memory.Span.Length; ++i)
                yield return memory.Span[i];
        }

        public static IEnumerable<T> EnumerateSpan<T>(this ReadOnlyMemory<T> memory)
        {
            for (var i = 0; i < memory.Span.Length; ++i)
                yield return memory.Span[i];
        }
    }
}
