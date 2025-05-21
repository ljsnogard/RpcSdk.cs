namespace SerdesKit.MessagePack
{
    using System;
    using System.Threading;

    using Cysharp.Threading.Tasks;
    using BufferKit;

    public sealed class BufferedWriter
    {
        private readonly TxProxy<byte> tx_;

        public BufferedWriter(TxProxy<byte> tx)
            => this.tx_ = tx;

        public UniTask<NUsize> WriteU8Async(byte data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteI8Async(sbyte data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteU16Async(ushort data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteI16Async(short data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteU32Async(uint data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteI32Async(uint data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteU64Async(ulong data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteI64Async(long data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteBoolAsync(bool data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteCharsync(char data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteF32Async(float data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteF64Async(double data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteF128Async(decimal data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteStringAsync(string data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteDateTimeOffsetAsync(DateTimeOffset data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteNilAsync(CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteArrayHeader(NUsize arrayItemCount, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<NUsize> WriteMapHeader(NUsize mapItemCount, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, Serializer<X>>> TryWriteAsync<X>(X data, CancellationToken token = default)
            => throw new NotImplementedException();
    }
}