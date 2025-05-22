namespace RpcClientSdk.Mar07
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    using BufferKit;

    using Cysharp.Threading.Tasks;

    using LoggingSdk;

    using RpcMuxSdk;

    public readonly struct SessionIoError : IIoError
    {
        public readonly string Repr;

        public SessionIoError(string repr)
            => this.Repr = repr;

        public Exception AsException()
            => new Exception(this.Repr);
    }

    public sealed class DemoSession
    {
        private readonly Port localPort_;

        private readonly Port remotePort_;

        private readonly MemoryStream rxBuff_;

        private readonly MemoryStream txBuff_;

        private readonly AsyncMutex rxMutex_;

        private readonly AsyncMutex txMutex_;

        private readonly CancellationTokenSource shutdownSignal_;

        private Task txLoopTask_;

        public DemoSession(NUsize bufferSize)
        {
            if (!bufferSize.TryInto(out int i32BufferSize))
                throw new ArgumentOutOfRangeException(nameof(i32BufferSize));

            this.rxBuff_ = new MemoryStream(i32BufferSize);
            this.txBuff_ = new MemoryStream(i32BufferSize);
            this.rxMutex_ = new();
            this.txMutex_ = new();
            this.shutdownSignal_ = new();
            this.txLoopTask_ = this.TxLoopAsync_(this.shutdownSignal_.Token);
        }

        public async UniTask<Result<NUsize, SessionIoError>> ReadAsync(Memory<byte> target, CancellationToken token = default)
        {
            var log = Logger.Shared;
            var readSize = NUsize.Zero;
            Option<AsyncMutex.Guard> optGuard = Option.None;
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
                    var i32CopySize = await this.rxBuff_.ReadAsync(dst, token);
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
                log.Error($"[{nameof(DemoSession)}.{nameof(ReadAsync)}] {ex}");
                throw;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        public async UniTask<Result<NUsize, SessionIoError>> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken token = default)
        {
            var log = Logger.Shared;
            var writtenSize = NUsize.Zero;
            Option<AsyncMutex.Guard> optGuard = Option.None;
            try
            {
                optGuard = await this.txMutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Result.Ok(writtenSize);

                await this.txBuff_.WriteAsync(source, token);
                writtenSize += source.NUsizeLength();
                return Result.Ok(writtenSize);
            }
            catch (OperationCanceledException)
            {
                return Result.Ok(writtenSize);
            }
            catch (Exception ex)
            {
                log.Error($"[{nameof(DemoSession)}.{nameof(WriteAsync)}] {ex}");
                throw;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        private async Task TxLoopAsync_(CancellationToken token)
        {
            while (true)
            {
                await Task.Yield();
                throw new NotImplementedException();
            }
        }
    }

    public readonly struct SessionRx : IUnbufferedInput<byte>
    {
        private readonly DemoSession session_;

        public UniTask<Result<NUsize, SessionIoError>> ReadAsync(Memory<byte> target, CancellationToken token = default)
            => this.session_.ReadAsync(target, token);

        async UniTask<Result<NUsize, IIoError>> IUnbufferedInput<byte>.ReadAsync(Memory<byte> target, CancellationToken token)
        {
            var r = await this.session_.ReadAsync(target, token);
            return r.MapErr(e => e as IIoError);
        }
    }

    public readonly struct SessionTx : IUnbufferedOutput<byte>
    {
        private readonly DemoSession session_;

        public UniTask<Result<NUsize, SessionIoError>> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken token = default)
            => this.session_.WriteAsync(source, token);

        async UniTask<Result<NUsize, IIoError>> IUnbufferedOutput<byte>.WriteAsync(ReadOnlyMemory<byte> source, CancellationToken token)
        {
            var r = await this.session_.WriteAsync(source, token);
            return r.MapErr(e => e as IIoError);
        }
    }
}
