namespace RpcMuxSdk
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    using NsAnyLR;
    using NsBufferKit;

    using Cysharp.Threading.Tasks;

    /// <summary>
    /// 发送端数据合成器，用于将多个 channel 的合成为帧流数据并通过输出端发送
    /// </summary>
    /// <typeparam name="T">底层 Output 所发送数据的类型，例如 <c>byte</c></typeparam>
    internal sealed partial class Compositor<T>
    {
        private readonly Arena<EnqueuedOutput> arena_;

        private readonly SimpleMux<T>.MuxContext muxCtx_;

        private readonly OutputProxy<T> output_;

        private readonly SemaphoreSlim sema_;

        /// <summary>
        /// 低优先级发送任务队列
        /// </summary>
        private readonly ConcurrentQueue<Arena<EnqueuedOutput>.Token> low_;

        /// <summary>
        /// 已发送待确认的任务有序集合
        /// </summary>
        private readonly AckQueue ackQueue_;

        private Compositor(Arena<EnqueuedOutput> arena, SimpleMux<T>.MuxContext muxContext, OutputProxy<T> output)
        {
            this.arena_ = arena;
            this.muxCtx_ = muxContext;
            this.output_ = output;
            this.sema_ = new(1, 1);

            this.low_ = new();
            this.ackQueue_ = new();
        }

        public static Compositor<T> Create(NUsize arenaCapacity, SimpleMux<T>.MuxContext muxContext, OutputProxy<T> output)
        {
            var arena = new Arena<EnqueuedOutput>((uint)arenaCapacity);
            return new Compositor<T>(arena, muxContext, output);
        }

        public UniTask<Result<NUsize, IIoError>> SendSignalAsync(
            ReadOnlyMemory<T> signalFrameData,
            Port localPort,
            Port remotePort,
            CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// 投递一个发送任务，发送内容由 datagram 指定; 发送最多一帧后，返回已发送的数据量。
        /// </summary>
        public UniTask<Result<NUsize, IIoError>> SendTelegraphDataAsync(
            RxProxy<T> rx,
            Port localPort,
            Port remotePort,
            CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// 投递一个发送任务，发送内容由 rx 指定，不会消耗 rx。
        /// 发送一帧或多帧后，返回已发送到数据量。
        /// 然后 rx 需自行通过 <c>ReaderSkipAsync</c> 方法移动指针。
        /// </summary>
        public UniTask<Result<NUsize, IIoError>> SendChannelDataAsync(
            RxProxy<T> rx,
            Port localPort,
            Port remotePort,
            uint serialNum,
            CancellationToken token = default)
        {
            throw new NotImplementedException();
        }
    }

    internal sealed partial class Compositor<T>
    {
        public struct EnqueuedOutput
        {
            private RxProxy<T> rx_;

            private Port localPort_;

            private Port remotePort_;

            /// <summary>
            /// 本端已确认 SN
            /// </summary>
            private uint serialNum_;

            /// <summary>
            /// 已发送待确认的长度，即 远端确认号 - 本端已确认 SN
            /// </summary>
            private uint ackLength_;

            public EnqueuedOutput(RxProxy<T> rx, Port localPort, Port remotePort, uint serialNum)
            {
                this.rx_ = rx;
                this.localPort_ = localPort;
                this.remotePort_ = remotePort;
                this.serialNum_ = serialNum;
                this.ackLength_ = 0;
            }

            public static void Init(ref EnqueuedOutput output, RxProxy<T> rx, Port localPort, Port remotePort, uint serialNum)
                => output = new EnqueuedOutput(rx, localPort, remotePort, serialNum);

            public RxProxy<T> Rx
                => this.rx_;

            public Port LocalPort
                => this.localPort_;

            public Port RemotePort
                => this.remotePort_;

            public uint SerialNum
            {
                get => this.serialNum_;
                internal set => this.serialNum_ = value;
            }

            internal uint AckLength
            {
                get => this.ackLength_;
                set => this.ackLength_ = value;
            }
        }

        private sealed class AckQueue
        {
            private readonly SemaphoreSlim sema_;

            private readonly SortedDictionary<uint, Arena<EnqueuedOutput>.Token> tokens_;

            public AckQueue()
            {
                this.sema_ = new(1, 1);
                this.tokens_ = new();
            }

            public async UniTask<Result<U, NUsize>> TryInspectLastAsync<U>(
                Func<Arena<EnqueuedOutput>.Token, U> inspect,
                CancellationToken cancelToken = default)
            {
                try
                {
                    await this.sema_.WaitAsync(cancelToken);
                    if (this.tokens_.Count == 0)
                        return Result.Err(NUsize.Zero);
                    var kv = this.tokens_.Last();
                    return Result.Ok(inspect(kv.Value));
                }
                finally
                {
                    if (this.sema_.CurrentCount == 0)
                        this.sema_.Release();
                }
            }

            public async UniTask<Result<uint, Arena<EnqueuedOutput>.Token>> TryEnqueueAsync(
                Arena<EnqueuedOutput>.Token arenaToken,
                CancellationToken cancelToken = default)
            {
                var ackLen = arenaToken.Map((in EnqueuedOutput output) => output.AckLength);
                if (ackLen == 0)
                    throw new ArgumentException($"AckLength({ackLen}) cannot be 0");
                try
                {
                    await this.sema_.WaitAsync(cancelToken);

                    var sn = arenaToken.ByRef().SerialNum;
                    if (this.tokens_.TryAdd(sn, arenaToken))
                        return Result.Ok(sn);
                    if (this.tokens_.TryGetValue(sn, out var duplication))
                        return Result.Err(duplication);
                    else
                        throw new Exception("Enqueue failed with unknown reasone");
                }
                finally
                {
                    if (this.sema_.CurrentCount == 0)
                        this.sema_.Release();
                }
            }

            public async UniTask<Result<Arena<EnqueuedOutput>.Token, NUsize>> TryDequeueAsync(
                Arena<EnqueuedOutput>.Token arenaToken,
                CancellationToken cancelToken = default)
            {
                try
                {
                    await this.sema_.WaitAsync(cancelToken);

                    var sn = arenaToken.ByRef().SerialNum;
                    if (this.tokens_.Remove(sn, out var removed))
                        return Result.Ok(removed);
                    else
                        return Result.Err((NUsize)this.tokens_.Count);
                }
                finally
                {
                    if (this.sema_.CurrentCount == 0)
                        this.sema_.Release();
                }
            }
        }
    }
}
