namespace RpcMuxSdk
{
    using System;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using BufferKit;

    public readonly struct TelegraphError
    { }

    /// <summary>
    /// 用于发送和接收单帧短消息，不需要提前建立连接的电报型通信设施
    /// </summary>
    public sealed class Telegraph<T>
    {
        private readonly PortBinder<T> portBinder_;

        internal Telegraph(PortBinder<T> portBinder)
        {
            this.portBinder_ = portBinder;
        }

        public UniTask<Result<NUsize, TelegraphError>> SendAsync(RxProxy<byte> packet, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<Result<RxProxy<byte>, TelegraphError>> RecvAsync(CancellationToken token = default)
            => throw new NotImplementedException();
    }
}
