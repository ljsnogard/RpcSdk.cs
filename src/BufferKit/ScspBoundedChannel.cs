namespace NsBufferKit
{
    using System;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using NsAnyLR;

    using LoggingSdk;

    /// <summary>
    /// Single consumer single producer bounded channel
    /// </summary
    public sealed class ScspBoundedChannel<T>
    {
        #region ScscpBoundedChannel Definition

        private AtomicU64Flags state_;

        private readonly Memory<T> items_;

        private readonly SemaphoreSlim consumerEvent_;

        private readonly SemaphoreSlim producerEvent_;

        private readonly AsyncMutex consumerMutex_;

        private readonly AsyncMutex producerMutex_;

        private readonly Consumer consumer_;

        private readonly Producer producer_;

        public ScspBoundedChannel(Memory<T> items)
        {
            if (items.Length == 0)
                throw new ArgumentException(message: "Zero length memory", paramName: nameof(items));

            this.state_ = new AtomicU64Flags(0uL);
            this.items_ = items;

            this.consumerEvent_ = new(0, 1);
            this.producerEvent_ = new(0, 1);

            this.consumerMutex_ = new();
            this.producerMutex_ = new();

            this.consumer_ = new Consumer(this);
            this.producer_ = new Producer(this);
        }

        public ScspBoundedChannel(uint capacity) : this(new Memory<T>(new T[capacity]))
        { }

        public NUsize Capacity
            => (NUsize)this.items_.Length;

        public async UniTask<Option<IConsumer>> GetConsumerAsync(CancellationToken token = default)
        {
            var maybeGuard = await this.consumerMutex_.AcquireAsync(token);
            if (maybeGuard.IsSome(out var guard))
            {
                this.consumer_.ResetGuard(guard);
                return Option.Some<IConsumer>(this.consumer_);
            }
            return Option.None();
        }

        public async UniTask<Option<IProducer>> GetProducerAsync(CancellationToken token = default)
        {
            var maybeGuard = await this.producerMutex_.AcquireAsync(token);
            if (maybeGuard.IsSome(out var guard))
            {
                this.producer_.ResetGuard(guard);
                return Option.Some<IProducer>(this.producer_);
            }
            return Option.None();
        }

        private void TrySendConsumerEvent_()
        {
            var log = Logger.Shared;
            var r = this.state_.TrySpinCompareExchange(
                ChannelState.ExpectRxAwaitFlagged,
                ChannelState.DesireRxAwaitUnflagged
            );
            if (!r.IsSucc(out var s))
                return;
            this.consumerEvent_.Release();
            log.Debug($"[{nameof(ScspBoundedChannel<T>)}.{nameof(TrySendConsumerEvent_)}] channel({this.GetHashCodeStrX8()}) consumer event occurring, {(ChannelState)s}");
        }

        private void TrySendProducerEvent_()
        {
            var log = Logger.Shared;
            var r = this.state_.TrySpinCompareExchange(
                ChannelState.ExpectTxAwaitFlagged,
                ChannelState.DesireTxAwaitUnflagged
            );
            if (!r.IsSucc(out var s))
                return;
            this.producerEvent_.Release();
            log.Debug($"[{nameof(ScspBoundedChannel<T>)}.{nameof(TrySendProducerEvent_)}] channel({this.GetHashCodeStrX8()}) producer event occurring, {(ChannelState)s}");
        }
        
        #endregion

        public readonly struct ChannelState
        {
            public readonly ulong Val;

            public const ulong K01_B63_IO_MUTEX_FLAG = 1uL << 63;

            public const ulong K01_B62_INVERSED_FLAG = 1uL << 62;

            public const ulong K01_B61_TX_AWAIT_FLAG = 1uL << 61;

            public const ulong K01_B60_RX_AWAIT_FLAG = 1uL << 60;

            public const ulong K31_B00_READER_POS_MASK = POSITION_MAX;

            public const ulong K31_B31_WRITER_POS_MASK = ((ulong)POSITION_MAX) << POS_BITS_WIDTH;

            public const int POS_BITS_WIDTH = 30;

            public const uint POSITION_MAX = (1u << POS_BITS_WIDTH) - 1;

            public const uint POSITION_ERR = 0xEEEE_EEEE;

            public ChannelState(ulong val)
                => this.Val = val;

            public uint Reader
                => ChannelState.GetReaderPosition(this.Val);

            public uint Writer
                => ChannelState.GetWriterPosition(this.Val);

            public bool IsInversed
                => ChannelState.ExpectInverseFlagged(this.Val);

            public bool IsMutexAcquired
                => ChannelState.ExpectIoMutexFlagged(this.Val);

            public bool IsTxAwait
                => ChannelState.ExpectTxAwaitFlagged(this.Val);

            public bool IsRxAwait
                => ChannelState.ExpectRxAwaitFlagged(this.Val);

            public static implicit operator ulong(ChannelState p)
                => p.Val;

            public static implicit operator ChannelState(ulong data)
                => new ChannelState(data);

            public override string ToString()
                => $"ChannelState({this.Val:x16}, Mutex: {this.IsMutexAcquired}, Inversed: {this.IsInversed}, TxAwait: {this.IsTxAwait}, RxAwait: {this.IsRxAwait}, Reader: {this.Reader}, Writer: {this.Writer})";

            #region ReaderPosition

            public static uint GetReaderPosition(ulong state)
                => unchecked((uint)(state & K31_B00_READER_POS_MASK));

            public static ulong SetReaderPosition(ulong state, uint position)
                => (state & (~K31_B00_READER_POS_MASK)) | ((ulong)position);

            public static bool ExpectReaderPositionCanPeek(ulong s0, uint capacity)
            {
                var r0 = ChannelState.GetReaderPosition(s0);
                var w0 = ChannelState.GetWriterPosition(s0);
                if (ChannelState.ExpectInverseFlagged(s0))
                    return r0 < capacity;
                else
                    return r0 < w0;
            }

            public static bool ExpectReaderPositionCanIncrease(ulong s0, uint capacity)
            {
                var r0 = ChannelState.GetReaderPosition(s0);
                var w0 = ChannelState.GetWriterPosition(s0);
                if (ChannelState.ExpectInverseFlagged(s0))
                    return capacity > r0;
                else
                    return w0 > r0;
            }

            public static ulong DesireReaderPositionIncreased(ulong s0, uint capacity)
            {
                var r0 = ChannelState.GetReaderPosition(s0);
                if (ChannelState.ExpectInverseFlagged(s0))
                {
                    if (capacity - r0 > 1)
                        return ChannelState.SetReaderPosition(s0, r0 + 1);

                    var s1 = ChannelState.SetReaderPosition(s0, 0);
                    return ChannelState.DesireInverseUnflagged(s1);
                }
                return ChannelState.SetReaderPosition(s0, r0 + 1);
            }

            #endregion

            #region WriterPosition

            public static uint GetWriterPosition(ulong state)
                => unchecked((uint)((state & K31_B31_WRITER_POS_MASK) >> POS_BITS_WIDTH));

            public static ulong SetWriterPosition(ulong state, uint position)
                => (state & (~K31_B31_WRITER_POS_MASK)) | (((ulong)position) << POS_BITS_WIDTH);

            public static bool ExpectWriterPositionCanIncrease(ulong s0, uint capacity)
            {
                var r0 = ChannelState.GetReaderPosition(s0);
                var w0 = ChannelState.GetWriterPosition(s0);
                if (ChannelState.ExpectInverseFlagged(s0))
                    return r0 > w0;
                return capacity > w0;
            }

            public static ulong DesireWriterPositionIncreased(ulong s0, uint capacity)
            {
                var r0 = ChannelState.GetReaderPosition(s0);
                var w0 = ChannelState.GetWriterPosition(s0);
                if (ChannelState.ExpectInverseFlagged(s0))
                    return ChannelState.SetWriterPosition(s0, w0 + 1);
                if (capacity - w0 > 1)
                    return ChannelState.SetWriterPosition(s0, w0 + 1);
                var s1 = ChannelState.SetWriterPosition(s0, 0);
                return ChannelState.DesireInverseFlagged(s1);
            }

            #endregion

            public static bool ExpectBothTxRxAwaitUnflagged(ulong state)
                => ExpectTxAwaitUnflagged(state) && ExpectRxAwaitUnflagged(state);

            #region TX_AWAIT Flag

            public static bool ExpectTxAwaitFlagged(ulong state)
                => (state & K01_B61_TX_AWAIT_FLAG) == K01_B61_TX_AWAIT_FLAG;

            public static bool ExpectTxAwaitUnflagged(ulong state)
                => !ExpectTxAwaitFlagged(state);

            public static ulong DesireTxAwaitFlagged(ulong state)
                => state | K01_B61_TX_AWAIT_FLAG;

            public static ulong DesireTxAwaitUnflagged(ulong state)
                => state & (~K01_B61_TX_AWAIT_FLAG);

            #endregion

            #region RX Close Flag

            public static bool ExpectRxAwaitFlagged(ulong state)
                => (state & K01_B60_RX_AWAIT_FLAG) == K01_B60_RX_AWAIT_FLAG;

            public static bool ExpectRxAwaitUnflagged(ulong state)
                => !ExpectRxAwaitFlagged(state);

            public static ulong DesireRxAwaitFlagged(ulong state)
                => state | K01_B60_RX_AWAIT_FLAG;

            public static ulong DesireRxAwaitUnflagged(ulong state)
                => state & (~K01_B60_RX_AWAIT_FLAG);

            #endregion

            #region IoMutex Flag

            public static bool ExpectIoMutexFlagged(ulong state)
                => (state & K01_B63_IO_MUTEX_FLAG) == K01_B63_IO_MUTEX_FLAG;

            public static bool ExpectIoMutexUnflagged(ulong state)
                => !ExpectIoMutexFlagged(state);

            public static ulong DesireIoMutexFlagged(ulong state)
                => state | K01_B63_IO_MUTEX_FLAG;

            public static ulong DesireIoMutexUnflagged(ulong state)
                => state & (~K01_B63_IO_MUTEX_FLAG);

            #endregion

            #region Inverse Flag

            public static bool ExpectInverseFlagged(ulong state)
                => (state & K01_B62_INVERSED_FLAG) == K01_B62_INVERSED_FLAG;

            public static bool ExpectInverseUnflagged(ulong state)
                => !ExpectInverseFlagged(state);

            public static ulong DesireInverseFlagged(ulong state)
                => state | K01_B62_INVERSED_FLAG;

            public static ulong DesireInverseUnflagged(ulong state)
                => state & (~K01_B62_INVERSED_FLAG);

            #endregion
        }

        public struct ScspIoMutexGuard : IDisposable
        {
            private readonly ScspBoundedChannel<T> channel_;

            private bool isDisposed_;

            private ScspIoMutexGuard(ScspBoundedChannel<T> channel)
            {
                this.channel_ = channel;
                this.isDisposed_ = false;
            }

            public static Option<ScspIoMutexGuard> TryAcquire(ScspBoundedChannel<T> channel)
            {
                var s = channel.state_.Read();
                var x = channel.state_.TryOnceCompareExchange(
                    s,
                    ChannelState.ExpectIoMutexUnflagged,
                    ChannelState.DesireIoMutexFlagged
                );
                if (x.IsSucc(out s))
                    return Option.Some(new ScspIoMutexGuard(channel));
                return Option.None();
            }

            public static Option<ScspIoMutexGuard> SpinAcquire(
                ScspBoundedChannel<T> channel,
                CancellationToken token = default)
            {
                var x = channel.state_.TrySpinCompareExchange(
                    ChannelState.ExpectIoMutexUnflagged,
                    ChannelState.DesireIoMutexFlagged,
                    token
                );
                if (x.IsSucc(out _))
                    return Option.Some(new ScspIoMutexGuard(channel));
                return Option.None();
            }

            public void Dispose()
            {
                var log = Logger.Shared;
                if (this.isDisposed_)
                    return;
                try
                {
                    var x = this.channel_.state_.TrySpinCompareExchange(
                        ChannelState.ExpectIoMutexFlagged,
                        ChannelState.DesireIoMutexUnflagged
                    );
                    if (x.IsSucc(out var s))
                        return;
                    log.Error($"[{nameof(ScspIoMutexGuard)}.{nameof(Dispose)}] Releasing ScspIoMutexGuard Failed: {(ChannelState)s}");
                }
                finally
                {
                    this.isDisposed_ = true;
                }
            }
        }

        #region IConsumer, IProducer and their implementations

        public interface IChannelToken : IDisposable
        {
            public T GetValue();

            public ref T GetByRef();
        }

        public interface IConsumer : IDisposable
        {
            public bool IsPairedWith(IProducer producer);

            public Option<IChannelToken> TryPeek();

            public UniTask<Option<IChannelToken>> PeekAsync(CancellationToken token = default);

            public Option<IChannelToken> TryDequeue();

            public UniTask<Option<IChannelToken>> DequeueAsync(CancellationToken token = default);
        }

        public interface IProducer : IDisposable
        {
            public bool IsPairedWith(IConsumer consumer);

            public Option<T> TryEnqueue(Func<T> factory);

            public bool TryEnqueue(T data)
                => this.TryEnqueue(() => data).IsSome();

            public UniTask<Option<T>> EnqueueAsync(Func<T> factory, CancellationToken token = default);

            public async UniTask<bool> EnqueueAsync(T data, CancellationToken token = default)
            {
                var opt = await this.EnqueueAsync(() => data, token);
                return opt.IsSome();
            }
        }

        private abstract class Operator : IDisposable
        {
            private readonly ScspBoundedChannel<T> channel_;

            private readonly AsyncMutex mutex_;

            /// <summary>
            /// 用于保存来自 ScspBoundedChannel consumerMutex 或 producerMutex 的 MutexSlim.Guard
            /// </summary>
            private Option<AsyncMutex.Guard> optGuard_;

            public Operator(ScspBoundedChannel<T> channel)
            {
                this.channel_ = channel;
                this.mutex_ = new();
                this.optGuard_ = Option.None();
            }

            public ScspBoundedChannel<T> Channel
                => this.channel_;

            public ulong LoadChannelStateValue()
                => this.Channel.state_.Read();

            public void ResetGuard(AsyncMutex.Guard guard)
            {
                lock(this)
                {
#if DEBUG
                    if (this.optGuard_.IsSome(out _))
                        throw new InvalidOperationException();
#endif
                    this.optGuard_ = Option.Some(guard);
                }
            }

            public void Dispose()
            {
                lock (this)
                {
                    if (!this.optGuard_.IsSome(out var guard))
                        return;
                    this.optGuard_ = Option.None();
                    guard.Dispose();
                }
            }

            protected AsyncMutex Mutex
                => this.mutex_;
        }

        private sealed class Consumer : Operator, IConsumer, IDisposable
        {
            private readonly ChannelToken consumerToken_;

            public Consumer(ScspBoundedChannel<T> channel) : base(channel)
                => this.consumerToken_ = new ChannelToken(channel);

            public bool IsPairedWith(IProducer producer)
                => producer is Operator p && object.ReferenceEquals(this.Channel, p.Channel);

            public Option<IChannelToken> TryPeek()
            {
                return this.TryRx_(PrepareToken);

                IChannelToken PrepareToken(AsyncMutex.Guard guard, uint index)
                {
                    this.consumerToken_.ResetGuard(guard, DoNothingOnTokenDispose, index);
                    return this.consumerToken_;
                }
            }

            public Option<IChannelToken> TryDequeue()
            {
                return this.TryRx_(PrepareToken);

                IChannelToken PrepareToken(AsyncMutex.Guard guard, uint index)
                {
                    this.consumerToken_.ResetGuard(guard, ReleaseOnTokenDispose, index);
                    return this.consumerToken_;
                }
            }

            public async UniTask<Option<IChannelToken>> PeekAsync(CancellationToken token = default)
            {
                return await this.RxAsync_(PrepareToken, token);

                IChannelToken PrepareToken(AsyncMutex.Guard guard, uint index)
                {
                    this.consumerToken_.ResetGuard(guard, DoNothingOnTokenDispose, index);
                    return this.consumerToken_;
                }
            }

            public async UniTask<Option<IChannelToken>> DequeueAsync(CancellationToken token = default)
            {
                return await this.RxAsync_(PrepareToken, token);

                IChannelToken PrepareToken(AsyncMutex.Guard guard, uint index)
                {
                    this.consumerToken_.ResetGuard(guard, ReleaseOnTokenDispose, index);
                    return this.consumerToken_;
                }
            }

            private Result<uint, ulong> TryGetReaderPosition_(ulong s0, uint capacity)
            {
                var optGuard = ScspIoMutexGuard.SpinAcquire(this.Channel);
                if (!optGuard.IsSome(out var guard))
                    return Result.Err(s0);
                using (guard)
                {
                    if (ChannelState.ExpectReaderPositionCanPeek(s0, capacity))
                        return Result.Ok(ChannelState.GetReaderPosition(s0));
                    else
                        return Result.Err(s0);
                }
            }

            private Option<IChannelToken> TryRx_(Func<AsyncMutex.Guard, uint, IChannelToken> createToken)
            {
                if (!this.Channel.Capacity.TryInto(out uint u32Capacity))
                    throw new NotSupportedException();

                var optGuard = this.Mutex.TryAcquire();
                if (!optGuard.IsSome(out var guard))
                    return Option.None();

                var isSucc = false;
                try
                {
                    var s = this.Channel.state_.Read();
                    var x = this.TryGetReaderPosition_(s, u32Capacity);
                    if (!x.IsOk(out var rp))
                        return Option.None();

                    IChannelToken tok = createToken(guard, rp);
                    isSucc = true;
                    return Option.Some(tok);
                }
                finally
                {
                    if (!isSucc)
                        guard.Dispose();
                }
            }

            private async UniTask<Option<IChannelToken>> RxAsync_(
                Func<AsyncMutex.Guard, uint, IChannelToken> prepareToken,
                CancellationToken token = default)
            {
                var log = Logger.Shared;
                if (!this.Channel.Capacity.TryInto(out uint u32Capacity))
                    throw new NotSupportedException();

                var optGuard = await base.Mutex.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Option.None();
                var isSucc = false;
                try
                {
                    var s = this.Channel.state_.Read();
                    while (true)
                    {
                        var x = this.TryGetReaderPosition_(s, u32Capacity);
                        if (x.IsOk(out var rp))
                        {
                            IChannelToken tok = prepareToken(guard, rp);
                            isSucc = true;
                            return Option.Some(tok);
                        }
                        if (token.IsCancellationRequested)
                            return Option.None();
                        if (x.IsErr(out s))
                        {
                            var r = this.Channel.state_.TrySpinCompareExchange(
                                expect: ChannelState.ExpectBothTxRxAwaitUnflagged,
                                desire: ChannelState.DesireRxAwaitFlagged,
                                token
                            );
                            if (!r.IsSucc(out s))
                                throw new Exception($"[{nameof(Consumer)}.{nameof(RxAsync_)}] failed setting RX_AWAIT flagged. {(ChannelState)s}");

                            await this.Channel.consumerEvent_.WaitAsync(token);
                            s = this.Channel.state_.Read();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    var r = this.Channel.state_.TrySpinCompareExchange(
                        expect: ChannelState.ExpectRxAwaitFlagged,
                        desire: ChannelState.DesireRxAwaitUnflagged,
                        token
                    );
                    return Option.None();
                }
                catch (Exception e)
                {
                    log.Error($"[{nameof(Consumer)}.{nameof(RxAsync_)}] unexpected exception: {e}");
                    throw;
                }
                finally
                {
                    if (!isSucc)
                        guard.Dispose();
                }
            }

            private static void ReleaseOnTokenDispose(ScspBoundedChannel<T> channel)
            {
                var log = Logger.Shared;
                var capacity = (uint)channel.Capacity;
                var x = channel.state_.TrySpinCompareExchange(
                    s => ChannelState.ExpectReaderPositionCanIncrease(s, capacity),
                    s => ChannelState.DesireReaderPositionIncreased(s, capacity)
                );
                if (!x.IsSucc(out var s))
                    throw new Exception($"[{nameof(Consumer)}.{nameof(ReleaseOnTokenDispose)}]");

                log.Debug($"[{nameof(Consumer)}.{nameof(ReleaseOnTokenDispose)}] channel({channel.GetHashCodeStrX8()}) after incr, {(ChannelState)s}");

                channel.TrySendProducerEvent_();
            }

            private static void DoNothingOnTokenDispose(ScspBoundedChannel<T> channel)
            {}
        }

        private sealed class Producer : Operator, IProducer, IDisposable
        {
            public Producer(ScspBoundedChannel<T> channel) : base(channel)
            {}

            public bool IsPairedWith(IConsumer consumer)
                => consumer is Operator c && object.ReferenceEquals(this.Channel, c.Channel);

            public Option<T> TryEnqueue(Func<T> factory)
            {
                if (!this.Channel.Capacity.TryInto(out uint u32Capacity))
                    throw new NotSupportedException();

                var s = this.Channel.state_.Read();
                var x = this.TryEnqueue_(s, u32Capacity, factory);
                if (x.IsSucc(out s))
                {
                    var wp = (int)ChannelState.GetWriterPosition(s);
                    return Option.Some(this.Channel.items_.Span[wp]);
                }
                return Option.None();
            }

            public async UniTask<Option<T>> EnqueueAsync(Func<T> factory, CancellationToken token = default)
            {
                var log = Logger.Shared;
                if (!this.Channel.Capacity.TryInto(out uint u32Capacity))
                    throw new NotSupportedException();

                var optGuard = await base.Mutex.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Option.None();
                try
                {
                    var s = this.Channel.state_.Read();
                    while (true)
                    {
                        var x = this.TryEnqueue_(s, u32Capacity, factory);
                        if (x.IsSucc(out s))
                        {
                            var wp = ChannelState.GetWriterPosition(s);
                            return Option.Some(this.Channel.items_.Span[(int)wp]);
                        }

                        log.Debug($"[{nameof(Producer)}.{nameof(EnqueueAsync)}] channel({this.Channel.GetHashCodeStrX8()}) TryEnqueue_ not succ: {(ChannelState)s}]");

                        if (token.IsCancellationRequested)
                            return Option.None();

                        if (x.IsUnexpected(out s))
                        {
                            var r = this.Channel.state_.TrySpinCompareExchange(
                                expect: ChannelState.ExpectBothTxRxAwaitUnflagged,
                                desire: ChannelState.DesireTxAwaitFlagged,
                                token
                            );
                            if (!r.IsSucc(out s))
                                throw new Exception($"[{nameof(Consumer)}.{nameof(EnqueueAsync)}] failed setting TX_AWAIT flagged. {(ChannelState)s}");
                            // cancellation may happen here, and then we must reset TX_AWAIT flag.

                            log.Debug($"[{nameof(Producer)}.{nameof(EnqueueAsync)}] channel({this.Channel.GetHashCodeStrX8()}) waiting producer event]");

                            await this.Channel.producerEvent_.WaitAsync(token);

                            log.Debug($"[{nameof(Producer)}.{nameof(EnqueueAsync)}] channel({this.Channel.GetHashCodeStrX8()}) producer event arrived]");

                            s = this.Channel.state_.Read();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return Option.None();
                }
                catch (Exception e)
                {
                    log.Error($"[{nameof(Consumer)}.{nameof(EnqueueAsync)}] unexpected exception: {e}");
                    throw;
                }
                finally
                {
                    // if cancellation happened during awaiting producer event, we have to remove the TX_AWAIT flag.
                    // However, the TX_AWAIT may be not flagged, and we don't have to guarantee the operation is succ. 
                    var r = this.Channel.state_.TrySpinCompareExchange(
                        expect: ChannelState.ExpectTxAwaitFlagged,
                        desire: ChannelState.DesireTxAwaitUnflagged
                    );
                    guard.Dispose();
                }
            }

            /// <summary>
            /// 尝试标记 IoMutex Flag 并检查能否推进 Producer 位置。意味着在填入数据运行 factory 时带有锁。
            /// </summary>
            private CmpXchResult<ulong> TryEnqueue_(
                ulong s0,
                uint capacity,
                Func<T> factory)
            {
                var log = Logger.Shared;
                var isIoMutexGuardAcquired = false;
                try
                {
                    var x = this.Channel.state_.TryOnceCompareExchange(
                        s0,
                        ExpectCanEnqueue_,
                        DesireIncreased_
                    );

                    log.Debug($"[{nameof(Producer)}.{nameof(TryEnqueue_)}] x: {x.ToString(v => $"{v:x16}")}, s: {(ChannelState)x.Data}");

                    if (x.IsSucc(out var s))
                    {
                        isIoMutexGuardAcquired = true;
                        var wp = (int)ChannelState.GetWriterPosition(s);
                        this.Channel.items_.Span[wp] = factory();
                        this.Channel.TrySendConsumerEvent_();
                    }
                    return x;
                }
                catch (Exception e)
                {
                    log.Error($"[{nameof(Producer)}.{nameof(TryEnqueue_)}] unexpected exception: {e}");
                    throw;
                }
                finally
                {
                    if (isIoMutexGuardAcquired)
                    {
                        var x = this.Channel.state_.TrySpinCompareExchange(
                            expect: ChannelState.ExpectIoMutexFlagged,
                            desire: ChannelState.DesireIoMutexUnflagged
                        );
                        if (!x.IsSucc(out var s))
                            throw new Exception($"[{nameof(Producer)}.{nameof(TryEnqueue_)}] failed releasing IoMutexGuard: {(ChannelState)s}");
                    }
                }

                bool ExpectCanEnqueue_(ulong s)
                {
                    return ChannelState.ExpectIoMutexUnflagged(s)
                        && ChannelState.ExpectWriterPositionCanIncrease(s, capacity);
                }

                ulong DesireIncreased_(ulong s)
                {
                    var s0 = ChannelState.DesireIoMutexFlagged(s);
                    return ChannelState.DesireWriterPositionIncreased(s0, capacity);
                }
                
            }
        }

        private sealed class ChannelToken : IChannelToken
        {
            private readonly ScspBoundedChannel<T> channel_;

            private Action<ScspBoundedChannel<T>>? onDispose_;

            private Option<AsyncMutex.Guard> optGuard_;

            private uint index_;

            public ChannelToken(ScspBoundedChannel<T> channel)
            {
                this.channel_ = channel;
                this.onDispose_ = null;
                this.optGuard_ = Option.None();
                this.index_ = ChannelState.POSITION_ERR;
            }

            public T GetValue()
                => this.GetByRef();

            public ref T GetByRef()
            {
                if (!this.channel_.Capacity.TryInto(out uint u32Capacity))
                    throw new NotSupportedException();

                var s = this.channel_.state_.Read();
                if (!ChannelState.ExpectReaderPositionCanPeek(s, u32Capacity))
                    throw new InvalidOperationException();

                var rp = ChannelState.GetReaderPosition(s);
                return ref this.channel_.items_.Span[(int)this.index_];
            }

            public void ResetGuard(
                AsyncMutex.Guard guard,
                Action<ScspBoundedChannel<T>> onDispose,
                uint index)
            {
                lock(this)
                {
#if DEBUG
                    if (this.optGuard_.IsSome(out var prevGuard) || this.onDispose_ is not null)
                        throw new InvalidOperationException($"[{nameof(ChannelToken)}.{nameof(ResetGuard)}]");
#endif
                    this.optGuard_ = Option.Some(guard);
                    this.onDispose_ = onDispose;
                    this.index_ = index;
                }
            }

            public void Dispose()
            {
                lock(this)
                {
                    if (!this.optGuard_.IsSome(out var guard))
                        return;
                    if (this.onDispose_ is not Action<ScspBoundedChannel<T>> onDispose)
                        return;
                    this.index_ = ChannelState.POSITION_ERR;
                    this.optGuard_ = Option.None();
                    this.onDispose_ = null;

                    onDispose(this.channel_);
                    guard.Dispose();
                }
            }
        }

        #endregion
    }
}