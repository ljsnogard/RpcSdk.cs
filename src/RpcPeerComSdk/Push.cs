namespace RpcPeerComSdk
{
    using System;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using NsAnyLR;
    using NsBufferKit;

    public interface IPushError
    {
        public Exception AsException();
    }

    public interface IPushAgent
    {
        public Uri Location { get; }

        /// <summary>
        /// 向远端（服务器）推送一个消息，该消息的全部内容以 Rx 的形式封装。
        /// </summary>
        public UniTask<Result<NUsize, IPushError>> SendAsync(RxProxy<byte> rx, CancellationToken token = default);
    }

    public interface IPushAgent<TItem>: IPushAgent
    {
        public UniTask<Result<NUsize, IPushError>> EnqueueAsync(TItem item, CancellationToken token = default);
    }
}
