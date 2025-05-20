namespace BufferKit
{
    using System;

    public interface IReclaim<T>
    { }

    public interface IReclaimReadOnlyMemory<T>: IReclaim<T>
    {
        public void Reclaim(ReadOnlyMemory<ReadOnlyMemory<T>> mem, NUsize offset);
    }

    public interface IReclaimMutableMemory<T>: IReclaim<T>
    {
        public void Reclaim(ReadOnlyMemory<Memory<T>> mem, NUsize offset);
    }

    public sealed class NoReclaim<T>: IReclaimReadOnlyMemory<T>, IReclaimMutableMemory<T>
    {
        private static readonly NoReclaim<T> singleton_ = new NoReclaim<T>();

        public static NoReclaim<T> Shared
            => singleton_;

        public void Reclaim(ReadOnlyMemory<ReadOnlyMemory<T>> mem, NUsize offset)
            => DoNothing();

        public void Reclaim(ReadOnlyMemory<Memory<T>> mem, NUsize offset)
            => DoNothing();

        private static void DoNothing() { }
    }

    internal readonly struct ReclaimBuffSegm<T> : IReclaimReadOnlyMemory<T>, IReclaimMutableMemory<T>
    {
        private readonly IBuffSegm<T> source_;

        private ReclaimBuffSegm(IBuffSegm<T> source)
            => this.source_ = source;

        public static ReclaimBuffSegm<T> Create<S>(in S source) where S: class, IBuffSegm<T>
            => new ReclaimBuffSegm<T>(source);

        public void Reclaim(ReadOnlyMemory<ReadOnlyMemory<T>> mem, NUsize offset)
        {
            if (this.source_ is IReaderBuffSegm<T> source)
                source.Forward(offset);
            else
                throw this.source_.UnexpectedTypeException();
        }

        public void Reclaim(ReadOnlyMemory<Memory<T>> mem, NUsize offset)
        {
            if (this.source_ is IWriterBuffSegm<T> source)
                source.Forward(offset);
            else
                throw this.source_.UnexpectedTypeException();
        }
    }
}