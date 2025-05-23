namespace RpcPeerComSdk.Jun10
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using BufferKit;

    using RpcMuxSdk;
    using RpcPeerComSdk;
    using System.Threading.Tasks;

    public readonly struct ConnectionError
    { }

    public sealed class DemoConnection
    {
        private readonly Socket socket_;

        private readonly SocketInput input_;

        private readonly SocketOutput output_;

        private readonly AsyncMutex inputMutex_;

        private readonly AsyncMutex outputMutex_;

        private readonly SortedDictionary<ChannelId, SessionInfo> sessionDict_;

        private readonly IApiTypeBind apiTypeBind_;

        private readonly CancellationTokenSource gracefulShutdown_;

        public DemoConnection(Socket socket, IApiTypeBind apiTypeBind)
        {
            var (output, input) = SocketIo.Split(socket);

            this.socket_ = socket;
            this.input_ = input;
            this.output_ = output;
            this.inputMutex_ = new();
            this.outputMutex_ = new();
            this.sessionDict_ = new();
            this.apiTypeBind_ = apiTypeBind;
            this.gracefulShutdown_ = new CancellationTokenSource();
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

        private async UniTask TxLoopAsync_()
        {
            var token = this.gracefulShutdown_.Token;
            while (true)
            {

            }
        }
    }

    internal sealed class SessionInfo
    {
        public const int ST_ACTIVE = 0;

        public const int ST_CLOSE_WAIT_1 = 1;

        public const int ST_CLOSE_WAIT_2 = 2;

        public const int ST_EXPIRED = 3;

        private readonly DemoSession session_;

        private readonly AsyncMutex connTxMutex_;

        private readonly OutputProxy<byte> connTx_;

        private int status_;

        private Task? txLoopTask_;

        public DemoSession Session
            => this.session_;

        public int Status
            => this.status_;

        public DateTimeOffset LastActive { get; private set; }

        public SessionInfo
            ( DemoSession session
            , AsyncMutex txMutex
            , OutputProxy<byte> tx
            , int status)
        {
            this.session_ = session;
            this.connTxMutex_ = txMutex;
            this.connTx_ = tx;

            this.status_ = status;
            this.LastActive = DateTimeOffset.Now;
            this.txLoopTask_ = null;
        }
    }
}
