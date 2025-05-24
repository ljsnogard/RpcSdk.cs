namespace RpcPeerComSdk.Jun10
{
    using System;
    using System.Buffers.Binary;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;

    using NsAnyLR;
    using NsBufferKit;

    using Cysharp.Threading.Tasks;

    using LoggingSdk;

    using RpcMuxSdk;

    public readonly struct ConnectionError
    { }

    public static class StdHeader
    {
        public static readonly string K_TYPE_HEX_HEADER_KEY = "typeHex";

        public static readonly string K_MSG_SIZE_HEADER_KEY = "msgSize";
    }

    public sealed class DemoConnection
    {
        private readonly Socket socket_;

        private readonly SocketInput input_;

        private readonly SocketOutput output_;

        private readonly AsyncMutex inputMutex_;

        private readonly AsyncMutex outputMutex_;

        private readonly Cysharp.Threading.Tasks.Channel<CachedSessMsg> msgCache_;

        private readonly LocalPortManager localPortMgr_;

        private readonly SessionManager sessMgr_;

        private readonly CancellationTokenSource gracefulShutdown_;

        private readonly IApiTypeBind apiTypeBind_;

        private Task? rxLoopTask_;

        public DemoConnection(Socket socket, IApiTypeBind apiTypeBind)
        {
            var (output, input) = SocketIo.Split(socket);

            this.socket_ = socket;
            this.input_ = input;
            this.output_ = output;
            this.inputMutex_ = new();
            this.outputMutex_ = new();

            this.msgCache_ = Cysharp.Threading.Tasks.Channel.CreateSingleConsumerUnbounded<CachedSessMsg>();
            this.localPortMgr_ = new LocalPortManager();
            this.sessMgr_ = new SessionManager();

            this.apiTypeBind_ = apiTypeBind;
            this.gracefulShutdown_ = new CancellationTokenSource();
            this.rxLoopTask_ = null;
        }

        /// <summary>
        /// 创建一个新的会话
        /// </summary>
        /// <param name="remotePort"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        public async UniTask<Option<(SessionTx, SessionRx)>> NewSessionAsync(Port remotePort, CancellationToken token = default)
        {
            var optPort = await this.localPortMgr_.AllocateAsync(token);
            if (!optPort.IsSome(out var localPort))
                return Option.None();

            throw new NotImplementedException();
        }

        internal IApiTypeBind ApiTypeBind
            => this.apiTypeBind_;

        internal AsyncMutex OutputMutex
            => this.outputMutex_;

        internal AsyncMutex InputMutex
            => this.inputMutex_;

        internal InputProxy<byte> Input
            => this.input_.GetCachedProxy();

        internal OutputProxy<byte> Output
            => this.output_.GetCachedProxy();

        public static async UniTask<DemoConnection> ConnectAsync
            (IPEndPoint server
            , NUsize buffCapacity
            , IApiTypeBind apiTypeBind
            , CancellationToken token = default)
        {
            var socket = await BufferedSocket.ConnectAsync(server, token);
            return new(socket, apiTypeBind);
        }

        private async ValueTask NotifyRxClosed_()
        {
            await Task.CompletedTask;
        }

        private async ValueTask NotifyGracefulShutDownAsync_()
        {
            await Task.CompletedTask;
        }

        private async Task RxLoopAsync_()
        {
            var log = Logger.Shared;
            var token = this.gracefulShutdown_.Token;
            Option<AsyncMutex.Guard> optTxMutexGuard = Option.None();

            Memory<byte> sessionHeaderSpan = new byte[FrameHead.K_SIZE_SESS_HEAD];
            Memory<byte> localPortSpan;
            Memory<byte> remotePortSpan;
            Memory<byte> msgFlagSpan;
            Memory<byte> msgSizeSpan;

            if (true)
            {
                int offset = 0;
                localPortSpan = sessionHeaderSpan.Slice(start: offset, length: FrameHead.K_SIZE_PORT_DATA);
                offset += FrameHead.K_SIZE_PORT_DATA;

                remotePortSpan = sessionHeaderSpan.Slice(start: offset, length:FrameHead.K_SIZE_PORT_DATA);
                offset += FrameHead.K_SIZE_PORT_DATA;

                msgFlagSpan = sessionHeaderSpan.Slice(start: offset, length: FrameHead.K_SIZE_FLAG_DATA);
                offset += FrameHead.K_SIZE_FLAG_DATA;

                msgSizeSpan = sessionHeaderSpan.Slice(start: 10, length: FrameHead.K_SIZE_MSG_LENG);
                offset += FrameHead.K_SIZE_MSG_LENG;

                Debug.Assert(offset == FrameHead.K_SIZE_SESS_HEAD);
            }
            var localEp = this.socket_.LocalEndPoint;
            var remoteEp = this.socket_.RemoteEndPoint;
            try
            {
                while (true)
                {
                    if (token.IsCancellationRequested)
                        break;

                    var optHeaderSize = await this.input_.ReadAsync(sessionHeaderSpan, token);
                    if (!optHeaderSize.TryOk(out var readSize, out var socketIoErr))
                    {
                        if (socketIoErr.Data.TryOk(out var errCode, out var socketErr))
                        {
                            if (errCode == SocketIoError.ERR_CLOSED)
                            {
                                await this.NotifyRxClosed_();
                                break;
                            }
                        }
                        throw socketIoErr.AsException();
                    }
                    if (readSize != sessionHeaderSpan.NUsizeLength())
                    {
                        var m = "Read session header: insufficient data len: {readSize}";
                        throw new Exception(m);
                    }
                    var frameHead = new FrameHead(
                        localPort: BinaryPrimitives.ReadUInt32BigEndian(localPortSpan.Span),
                        remotePort: BinaryPrimitives.ReadUInt32BigEndian(remotePortSpan.Span),
                        msgFlag: new(BinaryPrimitives.ReadUInt16BigEndian(msgFlagSpan.Span)),
                        msgSize: BinaryPrimitives.ReadUInt16BigEndian(msgSizeSpan.Span)
                    );

                    Memory<byte> msgCont = new byte[frameHead.MsgSize];
                    var optReadMsgSize = await this.input_.ReadAsync(msgCont, token);
                    if (!optReadMsgSize.TryOk(out var readMsgSize, out socketIoErr))
                    {
                        if (socketIoErr.Data.TryOk(out var errCode, out var socketErr))
                        {
                            if (errCode == SocketIoError.ERR_CLOSED)
                            {
                                await this.NotifyRxClosed_();
                                break;
                            }
                        }
                        throw socketIoErr.AsException();
                    }
                    IEnumerable<ReadOnlyMemory<byte>> cont = new[] { (ReadOnlyMemory<byte>)msgCont };
                    var msg = new SessionMessage(cont);
                    var cachedMsg = new CachedSessMsg(frameHead, msg);

                    /// 将接收到的报文放入待处理队列
                    while (true)
                    {
                        if (token.IsCancellationRequested)
                            break;
                        if (this.msgCache_.Writer.TryWrite(cachedMsg))
                            break;
                        else
                            log.Warn("failed to enqueue CachedSessMsg");
                    }
                }
            }
            catch (Exception e)
            {
                log.Error($"[{nameof(DemoConnection)}.{nameof(RxLoopAsync_)}] (l: {localEp}, r: {remoteEp}) {e}");
                throw;
            }
            finally
            {
                await this.NotifyGracefulShutDownAsync_();
            }
        }

        private Task ConsumeCachedMsgAsync_()
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 在流上复用提供 channel 并控制 channel 生命周期
    /// </summary>
    internal readonly struct MessageFlags
    {
        public readonly ushort Value;

        public MessageFlags(ushort v)
            => this.Value = v;

        public bool Fin
            => (this.Value & (1u)) != 0;

        public bool Syn
            => (this.Value & (1u << 1)) != 0;

        public bool Rst
            => (this.Value & (1u << 2)) != 0;

        public bool Ack
            => (this.Value & (1u << 3)) != 0;

        public static MessageFlags Build
            (bool fin
            , bool syn
            , bool rst
            , bool ack)
        {
            throw new NotImplementedException();
        }
    }

    internal readonly struct FrameHead
    {
        public const int K_SIZE_SESS_HEAD = 12;
        public const int K_SIZE_PORT_DATA = 4;
        public const int K_SIZE_FLAG_DATA = 2;
        public const int K_SIZE_MSG_LENG = 2;

        public readonly Port LocalPort;

        public readonly Port RemotePort;

        public readonly MessageFlags MsgFlag;

        public readonly ushort MsgSize;

        public FrameHead
            ( Port localPort
            , Port remotePort
            , MessageFlags msgFlag
            , ushort msgSize)
        {
            this.LocalPort = localPort;
            this.RemotePort = remotePort;
            this.MsgFlag = msgFlag;
            this.MsgSize = msgSize;
        }
    }

    internal readonly struct CachedSessMsg
    {
        public readonly FrameHead Head;

        public readonly SessionMessage Message;

        public CachedSessMsg(FrameHead head, SessionMessage message)
        {
            this.Head = head;
            this.Message = message;
        }
    }

    internal sealed class LocalPortManager
    {
        private readonly AsyncMutex mutex_;

        private readonly Queue<Port> idlePorts_;

        private readonly SortedDictionary<Port, ChannelId> activePorts_;

        private Port nextPort_;

        public static Port StartPort
            => 1025u;

        public LocalPortManager()
        {
            this.mutex_ = new AsyncMutex();
            this.idlePorts_ = new();
            this.activePorts_ = new();
            this.nextPort_ = StartPort;
        }

        public async UniTask<Option<Port>> AllocateAsync(CancellationToken token = default)
        {
            Option<AsyncMutex.Guard> optGuard = Option.None();
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Option.None();

                if (this.idlePorts_.Count != 0)
                {
                    var port = this.idlePorts_.Dequeue();
                    return Option.Some(port);
                }
                while (true)
                {
                    if (this.activePorts_.ContainsKey(this.nextPort_))
                    {
                        this.nextPort_ = new Port(this.nextPort_.code + 1u);
                        if (token.IsCancellationRequested)
                            return Option.None();
                        else
                            continue;
                    }
                    var res = this.nextPort_;
                    this.nextPort_ = new Port(this.nextPort_.code + 1u);
                    return Option.Some(res);
                }
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        public async UniTask<Option<ChannelId>> FindChannelId(Port localPort, CancellationToken token = default)
        {
            Option<AsyncMutex.Guard> optGuard = Option.None();
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Option.None();
                if (!this.activePorts_.TryGetValue(localPort, out var chanId))
                    return Option.None();

                if (chanId.LocalPort != localPort)
                    throw new Exception($"Err port bind: chanId.LocalPort({chanId.LocalPort}), query({localPort})");

                return Option.Some(chanId);
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        public async UniTask<Option<Port>> ExpireAsync(Port port, CancellationToken token = default)
        {
            Option<AsyncMutex.Guard> optGuard = Option.None();
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Option.None();
                if (!this.activePorts_.TryGetValue(port, out var chanId))
                    return Option.None();
                if (!this.activePorts_.Remove(port))
                    throw new Exception($"port {port.code} is not active.");

                this.idlePorts_.Enqueue(port);
                return Option.Some(chanId.RemotePort);
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }
    }

    internal sealed class SessionManager
    {
        private readonly AsyncMutex mutex_;

        private readonly SortedDictionary<ChannelId, SessionInfo> sessionDict_;

        private readonly Queue<DemoSession> idleSessions_;

        public SessionManager()
        {
            this.mutex_ = new AsyncMutex();
            this.sessionDict_ = new();
            this.idleSessions_ = new();
        }
    }

    internal sealed class SessionInfo
    {
        public const int ST_ACTIVE = 0;

        public const int ST_CLOSE_WAIT_1 = 1;

        public const int ST_CLOSE_WAIT_2 = 2;

        public const int ST_EXPIRED = 3;

        private readonly AsyncMutex infoMutex_;

        private readonly DemoSession session_;

        private readonly AsyncMutex connTxMutex_;

        private readonly OutputProxy<byte> connTx_;

        private readonly CancellationTokenSource gracefulShutdown_;

        private int status_;

        private Task? txLoopTask_;

        public DemoSession Session
            => this.session_;

        public int Status
            => this.status_;

        public DateTimeOffset LastActive { get; private set; }

        public SessionInfo
            (DemoSession session
            , AsyncMutex txMutex
            , OutputProxy<byte> tx
            , int status)
        {
            this.infoMutex_ = new AsyncMutex();
            this.session_ = session;
            this.connTxMutex_ = txMutex;
            this.connTx_ = tx;
            this.gracefulShutdown_ = new CancellationTokenSource();

            this.status_ = status;
            this.LastActive = DateTimeOffset.MinValue;
            this.txLoopTask_ = null;
        }

        public async UniTask<bool> StartSessionAsync(CancellationToken token = default)
        {
            Option<AsyncMutex.Guard> optGuard = Option.None();
            try
            {
                optGuard = await this.infoMutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return false;

                if (this.txLoopTask_ is not null)
                    return false;

                this.txLoopTask_ = this.TxLoopAsync_();
                this.LastActive = DateTimeOffset.Now;
                return true;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        private async Task TxLoopAsync_()
        {
            var token = this.gracefulShutdown_.Token;
            Option<AsyncMutex.Guard> optTxMutexGuard = Option.None();

            Memory<byte> sessionHeaderSpan = new byte[10];

            Memory<byte> localPortSpan = sessionHeaderSpan.Slice(start: 0, length: 4);
            Memory<byte> remotePortSpan = sessionHeaderSpan.Slice(start: 4, length: 4);
            Memory<byte> msgSizeSpan = sessionHeaderSpan.Slice(start: 8);
            try
            {
                while (true)
                {
                    var optMsg = await this.session_.DequeueOutboundAsync(token);
                    if (!optMsg.IsSome(out var msg))
                        continue;

                    if (!msg.Size.TryInto(out ushort u16size))
                        throw new Exception();

                    optTxMutexGuard = await this.connTxMutex_.AcquireAsync(token);
                    if (!optTxMutexGuard.IsSome(out var txGuard))
                    {
                        if (token.IsCancellationRequested)
                            break;
                        else
                            continue;
                    }
                    BinaryPrimitives.WriteUInt32BigEndian(localPortSpan.Span, this.session_.LocalPort.code);
                    BinaryPrimitives.WriteUInt32BigEndian(remotePortSpan.Span, this.session_.RemotePort.code);
                    BinaryPrimitives.WriteUInt16BigEndian(msgSizeSpan.Span, u16size);

                    await this.connTx_.WriteAsync(sessionHeaderSpan, Option.None(), default);
                    var msgLenWritten = NUsize.Zero;
                    foreach (var segm in msg.Cont)
                    {
                        await this.connTx_.WriteAsync(segm, Option.None(), default);
                        msgLenWritten += segm.NUsizeLength();
                        if (msgLenWritten >= ushort.MaxValue)
                            break;
                    }

                    txGuard.Dispose();
                    optTxMutexGuard = Option.None();
                }
            }
            finally
            {
                if (optTxMutexGuard.IsSome(out var txGuard))
                    txGuard.Dispose();
            }
        }
    }
}
