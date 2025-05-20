namespace RpcClientSdk
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using OneOf;

    using RpcMuxSdk;
    using RpcPeerComSdk;

    public sealed partial class SmuxClient : IClient
    {
        private readonly SimpleMux<byte> smux_;

        public SmuxClient(SimpleMux<byte> smux)
        {
            this.smux_ = smux;
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
    }
}