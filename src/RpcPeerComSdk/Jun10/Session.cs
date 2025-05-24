namespace RpcPeerComSdk.Jun10
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using NsAnyLR;
    using NsBufferKit;

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
        private readonly Cysharp.Threading.Tasks.Channel<SessionMessage> inboundQueue_;

        private readonly Cysharp.Threading.Tasks.Channel<SessionMessage> outboundQueue_;

        private readonly AsyncMutex rxMutex_;

        private readonly AsyncMutex txMutex_;

        private readonly AsyncMutex sessionMutex_;

        private IApiTypeBind apiTypeBind_;

        private Option<(SessionTx, SessionRx)> optExtUser_;

        public DemoSession
            (IApiTypeBind apiTypeBind
            , Port localPort
            , Port remotePort
            , Stream rxStream
            , Stream txStream)
        {
            this.LocalPort = localPort;
            this.RemotePort = remotePort;

            this.inboundQueue_ = Cysharp.Threading.Tasks.Channel.CreateSingleConsumerUnbounded<SessionMessage>();
            this.outboundQueue_ = Cysharp.Threading.Tasks.Channel.CreateSingleConsumerUnbounded<SessionMessage>();

            this.rxMutex_ = new();
            this.txMutex_ = new();
            this.sessionMutex_ = new();

            this.apiTypeBind_ = apiTypeBind;
            this.optExtUser_ = Option.None();
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

        public IApiTypeBind ApiTypeBind
            => this.apiTypeBind_;

        /// <summary>
        /// 接收来自远端的消息 消息内容包括可能是 Request(AccessMethod, Uri, Header, Body) 也可能是 Resp(Status, Header, Body)
        /// </summary>
        public async UniTask<Result<NUsize, SessionIoError>> RecvAsync
            (Memory<SessionMessage> target
            , CancellationToken token = default)
        {
            var log = Logger.Shared;
            var recvSize = NUsize.Zero;
            Option<AsyncMutex.Guard> optGuard = Option.None();
            try
            {
                optGuard = await this.rxMutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Result.Ok(NUsize.Zero);

                var consumer = this.inboundQueue_.Reader;
                var targetLen = target.NUsizeLength();
                while (true)
                {
                    if (recvSize >= targetLen)
                        return Result.Ok(recvSize);
                    var msg = await consumer.ReadAsync(token);
                    target.Span[(int)recvSize] = msg;
                    recvSize += 1u;
                }
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
        public async UniTask<Result<NUsize, SessionIoError>> SendAsync
            (ReadOnlyMemory<SessionMessage> source
            , CancellationToken token = default)
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
                Option<SessionMessage> optLastSent = Option.None();
                while (true)
                {
                    if (sentSize >= sourceLen)
                        break;
                    var producer = this.outboundQueue_.Writer;
                    var curr = source.Span[(int)sentSize];
                    if (producer.TryWrite(curr))
                    {
                        sentSize += 1;
                        optLastSent = Option.Some(curr);
                    }
                }
                if (optLastSent.IsSome(out var lastSent))
                    await lastSent.Completion.AsUniTask().AttachExternalCancellation(token);

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
            Option<AsyncMutex.Guard> optGuard = Option.None();
            try
            {
                optGuard = await this.sessionMutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Option.None();

                if (!this.optExtUser_.IsSome(out var user))
                    return Option.None();

                (var txUser, var rxUser) = user;
                var txTask = txUser.ExpireAsync();
                var rxTask = rxUser.ExpireAsync();
                optGuard = Option.None();
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
            (IApiTypeBind apiTypeBind
            , Port localPort
            , Port remotePort
            , CancellationToken token = default)
        {
            Option<AsyncMutex.Guard> optGuard = Option.None();
            try
            {
                optGuard = await this.sessionMutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Option.None();

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

        internal async UniTask<Option<SessionMessage>> DequeueOutboundAsync(CancellationToken token = default)
        {
            try
            {
                var consumer = this.outboundQueue_.Reader;
                var message = await consumer.ReadAsync(token);
                return Option.Some(message);
            }
            catch (OperationCanceledException)
            {
                return Option.None();
            }
        }

        internal async UniTask<bool> EnqueueInboundAsync(SessionMessage message, CancellationToken token = default)
        {
            var producer = this.inboundQueue_.Writer;
            await UniTask.CompletedTask;
            while (true)
            {
                if (token.IsCancellationRequested)
                    return false;
                if (producer.TryWrite(message))
                    return true;
            }
        }
    }

    public readonly struct SessionMessage
    {
        public static NUsize MAX_MSG_SIZE
            => 1u << 16;

        public readonly NUsize Size;

        /// <summary>
        /// 一个消息可能使用多段数据来保存
        /// </summary>
        public readonly IEnumerable<ReadOnlyMemory<byte>> Cont;

        private readonly TaskCompletionSource<NUsize> completion_;

        public SessionMessage(IEnumerable<ReadOnlyMemory<byte>> cont)
        {
            var totalLen = NUsize.Zero;
            foreach (var segm in cont)
                totalLen += segm.NUsizeLength();

            if (totalLen >= MAX_MSG_SIZE)
                throw new Exception();

            this.Size = totalLen;
            this.Cont = cont;
            this.completion_ = new TaskCompletionSource<NUsize>();
        }

        internal Task Completion
            => this.completion_.Task;

        internal void SetCompleted(NUsize size)
            => this.completion_.TrySetResult(size);
    }

    public sealed class SessionRx : IUnbufferedInput<SessionMessage>
    {
        private DemoSession? session_;

        private readonly AsyncMutex mutex_;

        internal SessionRx(DemoSession session)
        {
            this.session_ = session;
            this.mutex_ = new();
        }

        public async UniTask<Result<NUsize, SessionIoError>> ReadAsync(Memory<SessionMessage> target, CancellationToken token = default)
        {
            Option<AsyncMutex.Guard> optGuard = Option.None();
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
            Option<AsyncMutex.Guard> optGuard = Option.None();
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

        async UniTask<Result<NUsize, IIoError>> IUnbufferedInput<SessionMessage>.ReadAsync(Memory<SessionMessage> target, CancellationToken token)
        {
            var r = await this.ReadAsync(target, token);
            return r.MapErr(e => e as IIoError);
        }
    }

    public sealed class SessionTx : IUnbufferedOutput<SessionMessage>
    {
        private DemoSession? session_;

        private readonly AsyncMutex mutex_;

        internal SessionTx(DemoSession session)
        {
            this.session_ = session;
            this.mutex_ = new();
        }

        public async UniTask<Result<NUsize, SessionIoError>> WriteAsync(ReadOnlyMemory<SessionMessage> source, CancellationToken token = default)
        {
            Option<AsyncMutex.Guard> optGuard = Option.None();
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
            Option<AsyncMutex.Guard> optGuard = Option.None();
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

        async UniTask<Result<NUsize, IIoError>> IUnbufferedOutput<SessionMessage>.WriteAsync(ReadOnlyMemory<SessionMessage> source, CancellationToken token)
        {
            var r = await this.WriteAsync(source, token);
            return r.MapErr(e => e as IIoError);
        }
    }
}
