namespace RpcClientSdk.Mar07
{
    using System;
    using System.IO;
    using System.Threading;

    using Cysharp.Threading.Tasks;
    using LoggingSdk;

    using NsAnyLR;
    using NsBufferKit;

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
        private readonly DemoClient client_;

        private readonly StreamInput rxInput_;

        private readonly StreamOutput rxOutput_;

        private readonly StreamInput txInput_;

        private readonly StreamOutput txOutput_;

        private readonly AsyncMutex rxMutex_;

        private readonly AsyncMutex txMutex_;

        public DemoSession
            ( DemoClient client
            , Port localPort
            , Port remotePort
            , Stream rxStream
            , Stream txStream)
        {
            (var rxOutput, var rxInput) = StreamIO.Split(rxStream);
            (var txOutput, var txInput) = StreamIO.Split(txStream);

            this.LocalPort = localPort;
            this.RemotePort = remotePort;

            this.client_ = client;
            this.rxInput_ = rxInput;
            this.rxOutput_ = rxOutput;

            this.txInput_ = txInput;
            this.txOutput_ = txOutput;

            this.rxMutex_ = new();
            this.txMutex_ = new();
        }

        public Port LocalPort
        {
            get;
            internal set;
        }

        public Port RemotePort
        {
            get;
            internal set;
        }

        internal IApiTypeBind ApiTypeBind
            => this.client_.ApiTypeBind;

        /// <summary>
        /// client 在此取出要发送的数据
        /// </summary>
        internal InputProxy<byte> TxInput
            => this.txInput_.GetCachedProxy();

        /// <summary>
        /// client 在此放入已接收的数据
        /// </summary>
        internal OutputProxy<byte> RxOutput
            => this.rxOutput_.GetCachedProxy();

        /// <summary>
        /// 接收来自远端的消息
        /// </summary>
        public async UniTask<Result<NUsize, SessionIoError>> RecvAsync(Memory<byte> target, CancellationToken token = default)
        {
            var log = Logger.Shared;
            var recvSize = NUsize.Zero;
            Option<AsyncMutex.Guard> optGuard = Option.None();
            try
            {
                optGuard = await this.rxMutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Result.Ok(recvSize);

                var targetLen = target.NUsizeLength();
                while (true)
                {
                    if (recvSize >= targetLen)
                        break;

                    var sizeToFill = targetLen - recvSize;
                    var dst = target.Slice(recvSize, sizeToFill);
                    var readRes = await this.rxInput_.ReadAsync(dst, token);
                    if (readRes.TryOk(out var readCount, out var err))
                        recvSize += readCount;
                    else
                        throw err.AsException();
                }
                return Result.Ok(recvSize);
            }
            catch (OperationCanceledException)
            {
                return Result.Ok(recvSize);
            }
            catch (Exception ex)
            {
                log.Error($"[{nameof(DemoSession)}.{nameof(RecvAsync)}] {ex}");
                throw;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        /// <summary>
        /// 向远端发送消息
        /// </summary>
        public async UniTask<Result<NUsize, SessionIoError>> SendAsync(ReadOnlyMemory<byte> source, CancellationToken token = default)
        {
            var log = Logger.Shared;
            var sentSize = NUsize.Zero;
            Option<AsyncMutex.Guard> optGuard = Option.None();
            try
            {
                optGuard = await this.txMutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Result.Ok(sentSize);

                var sourceLen = source.NUsizeLength();
                while (true)
                {
                    if (sentSize >= sourceLen)
                        break;

                    var sizeToSend = sourceLen - sentSize;
                    var src = source.Slice(sentSize, sizeToSend);
                    var writeRes = await this.txOutput_.WriteAsync(src, token);
                    if (writeRes.TryOk(out var writtenCount, out var err))
                        sentSize += writtenCount;
                    else
                        throw err.AsException();
                }
                return Result.Ok(sentSize);
            }
            catch (OperationCanceledException)
            {
                return Result.Ok(sentSize);
            }
            catch (Exception ex)
            {
                log.Error($"[{nameof(DemoSession)}.{nameof(SendAsync)}] {ex}");
                throw;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }
    }

    public readonly struct SessionRx : IUnbufferedInput<byte>
    {
        private readonly DemoSession session_;

        public UniTask<Result<NUsize, SessionIoError>> ReadAsync(Memory<byte> target, CancellationToken token = default)
            => this.session_.RecvAsync(target, token);

        async UniTask<Result<NUsize, IIoError>> IUnbufferedInput<byte>.ReadAsync(Memory<byte> target, CancellationToken token)
        {
            var r = await this.session_.RecvAsync(target, token);
            return r.MapErr(e => e as IIoError);
        }
    }

    public readonly struct SessionTx : IUnbufferedOutput<byte>
    {
        private readonly DemoSession session_;

        internal SessionTx(DemoSession session)
            => this.session_ = session;

        public UniTask<Result<NUsize, SessionIoError>> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken token = default)
            => this.session_.SendAsync(source, token);

        async UniTask<Result<NUsize, IIoError>> IUnbufferedOutput<byte>.WriteAsync(ReadOnlyMemory<byte> source, CancellationToken token)
        {
            var r = await this.session_.SendAsync(source, token);
            return r.MapErr(e => e as IIoError);
        }
    }
}
