namespace NsBufferKit
{
    using System;
    using System.IO;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using NsAnyLR;

    using LoggingSdk;

    public readonly struct MemStreamIoError : IIoError
    {
        private readonly Exception exception_;

        public MemStreamIoError(Exception exception)
            => this.exception_ = exception;

        public Exception AsException()
            => this.exception_;
    }

    internal readonly struct MapError<E> : IMap<E, IIoError>
        where E : struct, IIoError
    {
        public IIoError Map(E err)
            => err as IIoError;
    }

    public sealed class MemStreamIo : IUnbufferedInput<byte>, IUnbufferedOutput<byte>
    {
        private readonly MemoryStream stream_;

        private readonly AsyncMutex rxMutex_;

        private readonly AsyncMutex txMutex_;

        private MemStreamIo(MemoryStream stream)
        {
            this.stream_ = stream;
            this.rxMutex_ = new();
            this.txMutex_ = new();
        }

        public MemStreamIo(NUsize initCapacity)
        {
            if (!initCapacity.TryInto(out int i32Capacity))
                throw new ArgumentOutOfRangeException(nameof(initCapacity));
            this.stream_ = new MemoryStream(i32Capacity);
            this.rxMutex_ = new();
            this.txMutex_ = new();
        }

        public (MemStreamTx, MemStreamRx) Split()
            => (new MemStreamTx(this), new MemStreamRx(this));

        public async UniTask<Result<NUsize, MemStreamIoError>> ReadAsync(Memory<byte> target, CancellationToken token = default)
        {
            var log = Logger.Shared;
            var readSize = NUsize.Zero;
            Option<AsyncMutex.Guard> optGuard = Option.None();
            var targetSize = target.NUsizeLength();
            try
            {
                optGuard = await this.rxMutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Result.Ok(readSize);
                while (true)
                {
                    if (readSize >= targetSize)
                        break;
                    var sizeToFill = targetSize - readSize;
                    var dst = target.Slice(readSize);
                    var i32CopySize = await this.stream_.ReadAsync(dst, token);
                    if (!NUsize.TryFrom(i32CopySize).IsOk(out var copySize))
                        throw new Exception($"unexpected read count: {i32CopySize}");
                    readSize += copySize;
                }
                return Result.Ok(readSize);
            }
            catch (OperationCanceledException)
            {
                return Result.Ok(readSize);
            }
            catch (Exception ex)
            {
                log.Error($"[{nameof(MemStreamIo)}.{nameof(ReadAsync)}] {ex}");
                throw;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        public async UniTask<Result<NUsize, MemStreamIoError>> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken token = default)
        {
            var log = Logger.Shared;
            var writtenSize = NUsize.Zero;
            Option<AsyncMutex.Guard> optGuard = Option.None();
            try
            {
                optGuard = await this.txMutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Result.Ok(writtenSize);

                await this.stream_.WriteAsync(source, token);
                writtenSize += source.NUsizeLength();
                return Result.Ok(writtenSize);
            }
            catch (OperationCanceledException)
            {
                return Result.Ok(writtenSize);
            }
            catch (Exception ex)
            {
                log.Error($"[{nameof(MemStreamIo)}.{nameof(WriteAsync)}] {ex}");
                throw;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        async UniTask<Result<NUsize, IIoError>> IUnbufferedInput<byte>.ReadAsync(Memory<byte> target, CancellationToken token)
        {
            var r = await this.ReadAsync(target, token);
            return r.MapErr(e => e as IIoError);
        }

        async UniTask<Result<NUsize, IIoError>> IUnbufferedOutput<byte>.WriteAsync(ReadOnlyMemory<byte> source, CancellationToken token)
        {
            var r = await this.WriteAsync(source, token);
            return r.MapErr(e => e as IIoError);
        }
    }

    public readonly struct MemStreamRx : IUnbufferedInput<byte>
    {
        private readonly MemStreamIo streamIo_;

        public MemStreamRx(MemStreamIo memStreamIo)
            => this.streamIo_ = memStreamIo;

        public UniTask<Result<NUsize, MemStreamIoError>> ReadAsync(Memory<byte> target, CancellationToken token = default)
            => this.streamIo_.ReadAsync(target, token);

        UniTask<Result<NUsize, IIoError>> IUnbufferedInput<byte>.ReadAsync(Memory<byte> target, CancellationToken token)
            => (this.streamIo_ as IUnbufferedInput<byte>).ReadAsync(target, token);
    }

    public readonly struct MemStreamTx : IUnbufferedOutput<byte>
    {
        private readonly MemStreamIo streamIo_;

        public MemStreamTx(MemStreamIo memStreamIo)
            => this.streamIo_ = memStreamIo;

        public UniTask<Result<NUsize, MemStreamIoError>> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken token = default)
            => this.streamIo_.WriteAsync(source, token);

        UniTask<Result<NUsize, IIoError>> IUnbufferedOutput<byte>.WriteAsync(ReadOnlyMemory<byte> source, CancellationToken token)
            => (this.streamIo_ as IUnbufferedOutput<byte>).WriteAsync(source, token);
    }
}