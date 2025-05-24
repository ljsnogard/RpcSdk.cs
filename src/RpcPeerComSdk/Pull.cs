namespace RpcPeerComSdk
{
    using System;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using NsAnyLR;
    using NsBufferKit;

    public interface IPullError
    {
        public Exception AsException();
    }

    public interface IPullAgent
    {
        public Uri Location { get; }

        public UniTask<Result<RxProxy<byte>, IPullError>> RecvAsync(CancellationToken token = default);
    }

    public interface IPullAgent<TItem>: IPullAgent
    {
        public UniTask<Result<TItem, IPullError>> DequeueAsync(CancellationToken token = default);
    }
}
