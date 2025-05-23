namespace RpcPeerComSdk.Jun10
{
    using System;
    using System.Threading;

    using BufferKit;

    using Cysharp.Threading.Tasks;
    using LoggingSdk;

    using RpcMuxSdk;
    using System.IO;

    public readonly struct SessionIoError : IIoError
    {
        public readonly string Repr;

        public SessionIoError(string repr)
            => this.Repr = repr;

        public Exception AsException()
            => new Exception(this.Repr);
    }

    internal sealed class DemoSession
    {
        private readonly StreamInput rxInput_;

        private readonly StreamOutput rxOutput_;

        private readonly StreamInput txInput_;

        private readonly StreamOutput txOutput_;

        private readonly AsyncMutex rxMutex_;

        private readonly AsyncMutex txMutex_;

        private readonly AsyncMutex sessionMutex_;

        private IApiTypeBind apiTypeBind_;

        private Option<(SessionTx, SessionRx)> optExtUser_;

        public DemoSession
            ( IApiTypeBind apiTypeBind
            , Port localPort
            , Port remotePort
            , Stream rxStream
            , Stream txStream)
        {
            (var rxOutput, var rxInput) = StreamIO.Split(rxStream);
            (var txOutput, var txInput) = StreamIO.Split(txStream);

            this.LocalPort = localPort;
            this.RemotePort = remotePort;

            this.rxInput_ = rxInput;
            this.rxOutput_ = rxOutput;

            this.txInput_ = txInput;
            this.txOutput_ = txOutput;

            this.rxMutex_ = new();
            this.txMutex_ = new();
            this.sessionMutex_ = new();

            this.apiTypeBind_ = apiTypeBind;
            this.optExtUser_ = Option.None;
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
            => this.apiTypeBind_;

        /// <summary>
        /// client 在此取出要发送的数据，这些数据已包括 localPort, RemotePort, MsgLen
        /// </summary>
        internal InputProxy<byte> TxInput
            => this.txInput_.GetCachedProxy();

        /// <summary>
        /// client 在此放入已接收的数据，这些数据已去除 localPort, RemotePort, MsgLen
        /// </summary>
        internal OutputProxy<byte> RxOutput
            => this.rxOutput_.GetCachedProxy();

        /// <summary>
        /// 接收来自远端的消息 消息内容包括可能是 Request(AccessMethod, Uri, Header, Body) 也可能是 Resp(Status, Header, Body)
        /// </summary>
        public async UniTask<Result<NUsize, SessionIoError>> RecvAsync(Memory<byte> target, CancellationToken token = default)
        {
            var log = Logger.Shared;
            var recvSize = NUsize.Zero;
            Option<AsyncMutex.Guard> optGuard = Option.None;
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
        /// 向远端发送长度已知的消息，发送前会自动加入 localPort, remotePort, 消息长度
        /// </summary>
        public async UniTask<Result<NUsize, SessionIoError>> SendAsync(ReadOnlyMemory<byte> source, CancellationToken token = default)
        {
            var log = Logger.Shared;
            var sentSize = NUsize.Zero;
            Option<AsyncMutex.Guard> optGuard = Option.None;
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

        internal async UniTask<Option<(SessionTx, SessionRx)>> ExpireAsync(CancellationToken token = default)
        {
            Option<AsyncMutex.Guard> optGuard = Option.None;
            try
            {
                optGuard = await this.sessionMutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Option.None;

                if (!this.optExtUser_.IsSome(out var user))
                    return Option.None;

                (var txUser, var rxUser) = user;
                var txTask = txUser.ExpireAsync();
                var rxTask = rxUser.ExpireAsync();
                optGuard = Option.None;
                guard.Dispose();

                await UniTask.WhenAll(txTask, rxTask);
                return Option.Some((txUser, rxUser));
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        internal async UniTask<Option<(SessionTx, SessionRx)>> ReactivateAsync
            ( IApiTypeBind apiTypeBind
            , Port localPort
            , Port remotePort
            , CancellationToken token = default)
        {
            Option<AsyncMutex.Guard> optGuard = Option.None;
            try
            {
                optGuard = await this.sessionMutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Option.None;

                if (this.optExtUser_.IsSome())
                    throw new Exception("Cannot reactivate when this is not expired");

                this.apiTypeBind_ = apiTypeBind;
                this.LocalPort = localPort;
                this.RemotePort = remotePort;

                var txUser = new SessionTx(this);
                var rxUser = new SessionRx(this);

                this.optExtUser_ = Option.Some((txUser, rxUser));
                return this.optExtUser_;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }
    }

    public sealed class SessionRx : IUnbufferedInput<byte>
    {
        private DemoSession? session_;

        private readonly AsyncMutex mutex_;

        internal SessionRx(DemoSession session)
        {
            this.session_ = session;
            this.mutex_ = new();
        }

        public async UniTask<Result<NUsize, SessionIoError>> ReadAsync(Memory<byte> target, CancellationToken token = default)
        {
            Option<AsyncMutex.Guard> optGuard = Option.None;
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Result.Ok(NUsize.Zero);

                if (this.session_ is DemoSession s)
                    return await s.RecvAsync(target, token);
                else
                    return Result.Err(new SessionIoError("expired"));
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        internal async UniTask<bool> ExpireAsync(CancellationToken token = default)
        {
            Option<AsyncMutex.Guard> optGuard = Option.None;
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return false;
                this.session_ = null;
                return true;
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
    }

    public sealed class SessionTx : IUnbufferedOutput<byte>
    {
        private DemoSession? session_;

        private readonly AsyncMutex mutex_;

        internal SessionTx(DemoSession session)
        {
            this.session_ = session;
            this.mutex_ = new();
        }

        public async UniTask<Result<NUsize, SessionIoError>> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken token = default)
        {
            Option<AsyncMutex.Guard> optGuard = Option.None;
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Result.Ok(NUsize.Zero);

                if (this.session_ is DemoSession s)
                    return await s.SendAsync(source, token);
                else
                    return Result.Err(new SessionIoError("expired"));
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        internal async UniTask<bool> ExpireAsync(CancellationToken token = default)
        {
            Option<AsyncMutex.Guard> optGuard = Option.None;
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return false;
                this.session_ = null;
                return true;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        async UniTask<Result<NUsize, IIoError>> IUnbufferedOutput<byte>.WriteAsync(ReadOnlyMemory<byte> source, CancellationToken token)
        {
            var r = await this.WriteAsync(source, token);
            return r.MapErr(e => e as IIoError);
        }
    }
}
