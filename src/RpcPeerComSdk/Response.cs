namespace RpcPeerComSdk
{
    using System.Collections.Generic;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using BufferKit;

    using OneOf;
    using System;

    public interface IResponseError
    {
        public Exception AsException();
    }

    public interface IResponse
    {
        public ResponseStatus Status { get; }

        public IAsyncEnumerable<Header> Headers { get; }

        public IBuffRx<byte> Body { get; }
    }

    public interface IResponse<TBody>: IResponse
    {
        public UniTask<OneOf<TBody, IResponseError>> ReadBodyAsync(CancellationToken token = default);
    }
}
