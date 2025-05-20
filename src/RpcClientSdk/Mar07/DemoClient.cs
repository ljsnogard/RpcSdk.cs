namespace RpcPeerComSdk.Mar07
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using BufferKit;

    using OneOf;

    using RpcMuxSdk;
    using RpcPeerComSdk;
    using RpcClientSdk;

    public sealed class DemoClient : IClient, IPusherClient, IPullerClient
    {
        private readonly Socket socket_;

        private readonly InputProxy<byte> input_;

        private readonly OutputProxy<byte> output_;

        private readonly AsyncMutex inputMutex_;

        private readonly AsyncMutex outputMutex_;

        private readonly SortedDictionary<ChannelId, DemoSession> sessionDict_;

        public DemoClient(Socket socket)
        {
            this.socket_ = socket;
            var (output, input) = SocketIo.Split(socket);
            this.input_ = input.GetCachedProxy();
            this.output_ = output.GetCachedProxy();
            this.inputMutex_ = new();
            this.outputMutex_ = new();
            this.sessionDict_ = new SortedDictionary<ChannelId, DemoSession>();
        }

        internal AsyncMutex OutputMutex
            => this.outputMutex_;

        internal AsyncMutex InputMutex
            => this.inputMutex_;

        internal Socket Socket
            => this.socket_;

        public static async UniTask<DemoClient> ConnectAsync(
            IPEndPoint server,
            NUsize buffCapacity,
            CancellationToken token = default)
        {
            var socket = await BufferedSocket.ConnectAsync(server, token);
            return new(socket);
        }

        public UniTask<OneOf<IResponse<TResult>, IClientError>> RequestAsync<TReqeust, TResult>(
            AccessMethod accessMethod,
            Uri location,
            IAsyncEnumerable<Header> headers,
            TReqeust body,
            CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public UniTask<PushAgent> PushAsync(
            Uri location,
            CancellationToken token = default)
        {

            return UniTask.FromResult(
                new PushAgent(
                    this.output_,
                    location,
                    name: $"(l: {this.socket_.LocalEndPoint}, r: {this.socket_.RemoteEndPoint})"
            ));
        }

        public UniTask<PullAgent> PullAsync(
            Uri location,
            CancellationToken token = default)
        {
            return UniTask.FromResult(
                new PullAgent(
                    this.input_,
                    location,
                    name: $"(l: {this.socket_.LocalEndPoint}, r: {this.socket_.RemoteEndPoint})"
            ));
        }

        #region IPullerClient

        async UniTask<OneOf<IPullAgent, IClientError>> IPullerClient.PullAsync(
            Uri location,
            IAsyncEnumerable<Header> headers,
            CancellationToken token)
        {
            var agent = await this.PullAsync(location, token);
            return OneOf<IPullAgent, IClientError>.FromT0(agent);
        }

        #endregion

        #region IPusherClient

        async UniTask<OneOf<IPushAgent, IClientError>> IPusherClient.PushAsync(
            Uri location,
            IAsyncEnumerable<Header> headers,
            CancellationToken token)
        {
            var agent = await this.PushAsync(location, token);
            return OneOf<IPushAgent, IClientError>.FromT0(agent);
        }

        #endregion
    }
}
