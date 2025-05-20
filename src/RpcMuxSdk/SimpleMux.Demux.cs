namespace RpcMuxSdk
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    using BufferKit;

    using Cysharp.Threading.Tasks;

    using OneOf;

    /// <summary>
    /// 接收端数据分离器，用于从输入设备中接收和解析帧流数据，并投放到 channel 的接收队列中
    /// </summary>
    /// <typeparam name="T">底层 Input 所接收数据的类型，例如 <c>byte</c></typeparam>
    internal sealed class Demultiplexer<T>
    {
        private readonly SimpleMux<T>.MuxContext muxCtx_;

        private readonly InputProxy<T> input_;

        private readonly SemaphoreSlim sema_;

        private readonly SortedDictionary<ChannelId, TxProxy<T>> channels_;

        private readonly SortedDictionary<ChannelId, TxProxy<Memory<T>>> telegraphs_;

        public Demultiplexer(SimpleMux<T>.MuxContext muxContext, InputProxy<T> input)
        {
            this.muxCtx_ = muxContext;
            this.input_ = input;
            this.sema_ = new(1, 1);
            this.channels_ = new();
            this.telegraphs_ = new();
        }

        public async UniTask<bool> TryRegisterChannelAsync(
            Port localPort,
            Port remotePort,
            TxProxy<T> tx,
            CancellationToken token = default)
        {
            try
            {
                await this.sema_.WaitAsync(token);
                var channelId = new ChannelId(localPort, remotePort);
                return this.channels_.TryAdd(channelId, tx);
            }
            finally
            {
                if (this.sema_.CurrentCount == 0)
                    this.sema_.Release();
            }
        }

        public async UniTask<bool> TryUnregisterChannelAsync(
            Port localPort,
            Port remotePort,
            CancellationToken token = default)
        {
            try
            {
                await this.sema_.WaitAsync(token);
                var channelId = new ChannelId(localPort, remotePort);
                return this.channels_.Remove(channelId);
            }
            finally
            {
                if (this.sema_.CurrentCount == 0)
                    this.sema_.Release();
            }
        }
    }
}