namespace RpcMuxSdk
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    using BufferKit;

    using Cysharp.Threading.Tasks;

    using OneOf;

    public readonly struct SimpleMuxError
    { }

    /// <summary>
    /// 用于在一个全双工可靠传输信道上同时进行多个活动会话的连接
    /// </summary>
    public sealed partial class SimpleMux<T>
    {
        public const uint PRIORITY_LEVELS = 3;

        private readonly MuxContext muxCtx_;

        private readonly Compositor<T> compositor_;

        private readonly Demultiplexer<T> demultiplexer_;

        public SimpleMux(OutputProxy<T> output, InputProxy<T> input, MuxAgreement agreement)
        {
            this.muxCtx_ = new MuxContext(agreement);
            this.compositor_ = Compositor<T>.Create(arenaCapacity: 128, this.muxCtx_, output);
            this.demultiplexer_ = new Demultiplexer<T>(this.muxCtx_, input);
        }

        public UniTask<OneOf<PortBinder<T>, SimpleMuxError>> BindAsync(Port localPort, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<OneOf<Channel<T>, SimpleMuxError>> AcceptAsync(Descriptor<T> descriptor, CancellationToken token = default)
            => throw new NotImplementedException();

        public UniTask<OneOf<TxProxy<T>, SimpleMuxError>> RejectAsync(Descriptor<T> descriptor, CancellationToken token = default)
            => throw new NotImplementedException();
    }

    public sealed partial class SimpleMux<T>
    {
        internal sealed class MuxContext
        {
            private readonly MuxAgreement agreement_;

            /// <summary>
            /// Local Port Keyed dictionary
            /// </summary>
            private readonly Dictionary<Port, LinkedListNode<PortBinder<T>>> localPortsDict_;

            /// <summary>
            /// 最近活跃 port 的先进先出队列
            /// </summary>
            private readonly LinkedList<PortBinder<T>> localPortsQueue_;

            /// <summary>
            /// 闲置 Port 顺序列表
            /// </summary>
            private readonly List<Port> idlePortsQueue_;

            public MuxContext(MuxAgreement agreement)
            {
                this.agreement_ = agreement;
                this.localPortsDict_ = new();
                this.localPortsQueue_ = new();
                this.idlePortsQueue_ = new();
            }

            public MuxAgreement Agreement
                => this.agreement_;
        }
    }
}
