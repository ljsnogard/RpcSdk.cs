namespace SerdesKit.Json
{
    using System;
    using System.Threading;

    using Cysharp.Threading.Tasks;
    using BufferKit;

    public readonly struct DataWriter
    {
        private readonly TxProxy<byte> tx_;

        public DataWriter(TxProxy<byte> tx)
            => this.tx_ = tx;

        public UniTask<Result<NUsize, IIoError>> WriteU8Async(byte data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteI8Async(sbyte data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteU16Async(ushort data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteI16Async(short data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteU32Async(uint data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteI32Async(uint data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteU64Async(ulong data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteI64Async(long data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteBoolAsync(bool data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteCharsync(char data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteF32Async(float data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteF64Async(double data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteF128Async(decimal data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteStringAsync(string data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteDateTimeOffsetAsync(DateTimeOffset data, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteNilAsync(CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WritePropertyNameAndSep(string propertyName, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteArrayHead(NUsize arrayItemCount, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, IIoError>> WriteObjectHead(NUsize mapItemCount, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<NUsize, Serializer<X>>> TryWriteAsync<X>(X data, CancellationToken token = default)
            => throw new NotImplementedException();
    }
}