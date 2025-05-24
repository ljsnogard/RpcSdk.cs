namespace RpcClientSdk.Mar07
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using NsAnyLR;
    using NsBufferKit;

    using RpcMuxSdk;
    using RpcPeerComSdk;
    using RpcClientSdk;

    public sealed class DemoClient : IClient, IPusherClient, IPullerClient
    {
        private readonly Socket socket_;

        private readonly SocketInput input_;

        private readonly SocketOutput output_;

        private readonly AsyncMutex inputMutex_;

        private readonly AsyncMutex outputMutex_;

        private readonly SortedDictionary<ChannelId, DemoSession> sessionDict_;

        private readonly IApiTypeBind apiTypeBind_;

        public DemoClient(Socket socket, IApiTypeBind apiTypeBind)
        {
            var (output, input) = SocketIo.Split(socket);

            this.socket_ = socket;
            this.input_ = input;
            this.output_ = output;
            this.inputMutex_ = new();
            this.outputMutex_ = new();
            this.sessionDict_ = new SortedDictionary<ChannelId, DemoSession>();
            this.apiTypeBind_ = apiTypeBind;
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

        public static async UniTask<DemoClient> ConnectAsync<TApiTypeInit>
            ( IPEndPoint server
            , NUsize buffCapacity
            , CancellationToken token = default
            )
            where TApiTypeInit : IApiTypeInit, new()
        {
            var socket = await BufferedSocket.ConnectAsync(server, token);
            var apiTypeBind = new ApiTypeAssocCache<TApiTypeInit>();
            return new(socket, apiTypeBind);
        }

        public UniTask<Result<IResponse<TResult>, IClientError>> RequestAsync<TReqeust, TResult>
            ( AccessMethod accessMethod
            , Uri location
            , IAsyncEnumerable<Header> headers
            , TReqeust body
            , CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public UniTask<PushAgent> PushAsync(
            Uri location,
            CancellationToken token = default)
        {

            return UniTask.FromResult(
                new PushAgent(
                    this.Output,
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
                    this.Input,
                    location,
                    name: $"(l: {this.socket_.LocalEndPoint}, r: {this.socket_.RemoteEndPoint})",
                    this.apiTypeBind_
            ));
        }

        #region IPullerClient

        async UniTask<Result<IPullAgent, IClientError>> IPullerClient.PullAsync(
            Uri location,
            IAsyncEnumerable<Header> headers,
            CancellationToken token)
        {
            var agent = await this.PullAsync(location, token);
            return Result.Ok(agent as IPullAgent);
        }

        #endregion

        #region IPusherClient

        async UniTask<Result<IPushAgent, IClientError>> IPusherClient.PushAsync(
            Uri location,
            IAsyncEnumerable<Header> headers,
            CancellationToken token)
        {
            var agent = await this.PushAsync(location, token);
            return Result.Ok(agent as IPushAgent);
        }

        #endregion
    }

    public sealed class ApiCall<TArg, TRes> : ICallerClient<TArg, TRes>
    {
        private readonly DemoClient client_;

        internal ApiCall(DemoClient client)
            => this.client_ = client;

        public UniTask<Result<IResponse<TResult>, IClientError>> RequestAsync<TReqeust, TResult>(
            AccessMethod accessMethod,
            Uri location,
            IAsyncEnumerable<Header> headers,
            TReqeust body,
            CancellationToken token = default)
        {
            return this.client_.RequestAsync<TReqeust, TResult>(accessMethod, location, headers, body, token);
        }

        public async UniTask<Result<TRes, IClientError>> CallAsync(
            Uri location,
            IAsyncEnumerable<Header> headers,
            TArg arguments,
            CancellationToken token = default)
        {
            var reqRes = await this.RequestAsync<object?, TRes>(
                accessMethod: AccessMethod.Call,
                location: location,
                headers: headers,
                body: null,
                token: token
            );
            if (reqRes.TryOk(out var response, out var err))
                return Result.Err(err);
            throw new NotFiniteNumberException();
        }
    }
}
