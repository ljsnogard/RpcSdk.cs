namespace BufferKit
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;  // when Debug string.Join
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using Cysharp.Threading.Tasks;

    using LoggingSdk;

    public readonly struct RingBufferError : IIoError, IEquatable<RingBufferError>
    {
        private readonly uint code_;

        private RingBufferError(uint code)
            => this.code_ = code;

        public Exception AsException()
            => new Exception(this.ToString());

        internal static readonly ReadOnlyMemory<string> NAMES =
            new(new [] { nameof(Idle), nameof(Closed), nameof(Incapable), nameof(Cancelled) });

        internal static readonly RingBufferError Idle = new(0);

        public static readonly RingBufferError Closed = new(1);

        public static readonly RingBufferError Incapable = new(2);

        public static readonly RingBufferError Cancelled = new(3);

        public static bool operator ==(RingBufferError lhs, RingBufferError rhs)
            => lhs.code_ == rhs.code_;

        public static bool operator !=(RingBufferError lhs, RingBufferError rhs)
            => lhs.code_ != rhs.code_;

        public override string ToString()
            => $"RingBufferError.{NAMES.Span[(int)this.code_]}";

        public override bool Equals([NotNullWhen(true)] object? obj)
            => obj is RingBufferError err && this.code_ == err.code_;

        public override int GetHashCode()
            => HashCode.Combine(typeof(RingBufferError), this.code_);

        public bool Equals(RingBufferError other)
            => this.code_ == other.code_;
    }

    public sealed partial class RingBuffer<T> : IScspBuffer<T>
    {
        private readonly Memory<T> memory_;

        /// <summary>
        /// 控制写者并发数量上限为1
        /// </summary>
        private readonly AsyncMutex writerMutex_;

        /// <summary>
        /// 控制读者并发数量上限为1
        /// </summary>
        private readonly AsyncMutex readerMutex_;

        /// <summary>
        /// 用于唤醒等待的写者
        /// </summary>
        private readonly SemaphoreSlim writerSema_;

        /// <summary>
        /// 用于唤醒等待的读者
        /// </summary>
        private readonly SemaphoreSlim readerSema_;

        /// <summary>
        /// 共用的用于回收所借出缓冲区段落时调整读者位置的回调
        /// </summary>
        private readonly IReclaimReadOnlyMemory<T> reclaimReaderBuffSegm_;

        private readonly IReclaimMutableMemory<T> reclaimWriterBuffSegm_;

        private readonly IReclaimReadOnlyMemory<T> reclaimPeekerBuffSegm_;

        private readonly ReaderBuffSegm<T> readerSegm_;

        private readonly WriterBuffSegm<T> writerSegm_;

        private readonly ReadOnlyMemory<T>[] readerMem_;

        private readonly Memory<T>[] writerMem_;

        /// <summary>
        /// 缓冲区最大容量
        /// </summary>
        private readonly uint capacity_;

        private readonly AtomicState state_;

        private Option<Demand> pendingTxDemand_;

        private Option<Demand> pendingRxDemand_;

        private RingBuffer(Memory<T> memory, ulong initState = 0uL)
        {
            this.memory_ = memory;
            this.writerMutex_ = new();
            this.readerMutex_ = new();
            this.writerSema_ = new(0, 1);
            this.readerSema_ = new(0, 1);

            this.reclaimReaderBuffSegm_ = new ReclaimReaderBuffSegm(this);
            this.reclaimWriterBuffSegm_ = new ReclaimWriterBuffSegm(this);
            this.reclaimPeekerBuffSegm_ = NoReclaim<T>.Shared;

            this.readerSegm_ = ReaderBuffSegm<T>.Empty;
            this.writerSegm_ = WriterBuffSegm<T>.Empty;
            this.readerMem_ = new[] { ReadOnlyMemory<T>.Empty, ReadOnlyMemory<T>.Empty };
            this.writerMem_ = new[] { Memory<T>.Empty, Memory<T>.Empty };

            this.capacity_ = (uint)memory.Length;
            this.state_ = new(initState);

            this.pendingTxDemand_ = Option.None;
            this.pendingRxDemand_ = Option.None;
        }

        public RingBuffer(NUsize capacity) : this(new Memory<T>(new T[capacity]))
        { }

        public static RingBuffer<T> ForDebug(Memory<T> memory, ulong initState = 0uL)
            => new RingBuffer<T>(memory, initState);

        public Result<(TxProxy<T>, RxProxy<T>), RingBufferError> TrySplit(bool shouldCloseOnDispose = true)
        {
            if (this.IsTxClosed || this.IsRxClosed)
                return Result.Err(RingBufferError.Closed);

            var tx = this.GetCachedTxProxy(shouldCloseOnDispose ? MarkTxClosed : null);
            var rx = this.GetCachedRxProxy(shouldCloseOnDispose ? MarkRxClosed : null);
            return Result.Ok((tx, rx));
        }

        /// <summary>
        /// 缓冲区的最大数据容量
        /// </summary>
        public NUsize Capacity
            => this.capacity_;

        /// <summary>
        /// 用于检查缓冲区内部存储
        /// </summary>
        public ReadOnlyMemory<T> InternalMemory
            => this.memory_;

        /// <summary>
        /// 从内部缓冲区中借出一段可供消费者读取的已填充缓存，该缓存长度不大于给定的长度；如果执行完成，则返回该已填充缓存
        /// </summary>
        public UniTask<Result<ReaderBuffSegm<T>, RingBufferError>> ReadAsync(Demand demand, CancellationToken token = default)
            => this.RxAsync_(offset: NUsize.Zero, demand, this.CreateReaderSegm, token);

        public UniTask<Result<ReaderBuffSegm<T>, RingBufferError>> PeekAsync(NUsize offset, Demand demand, CancellationToken token = default)
            => this.RxAsync_(offset, demand, this.CreatePeekerSegm, token);

        public UniTask<Result<NUsize, RingBufferError>> ReaderSkipAsync(NUsize length, CancellationToken token = default)
            => throw new NotImplementedException();

        private ReaderBuffSegm<T> CreateReaderSegm(ReadOnlyMemory<ReadOnlyMemory<T>> m, AsyncMutex.Guard g)
        {
            this.readerSegm_.Reset(m, Option.Some(g), this.reclaimReaderBuffSegm_);
            return this.readerSegm_;
        }

        private ReaderBuffSegm<T> CreatePeekerSegm(ReadOnlyMemory<ReadOnlyMemory<T>> m, AsyncMutex.Guard g)
        {
            this.readerSegm_.Reset(m, Option.Some(g), this.reclaimPeekerBuffSegm_);
            return this.readerSegm_;
        }

        private delegate ReaderBuffSegm<T> FnResetSegm
            ( ReadOnlyMemory<ReadOnlyMemory<T>> memory
            , AsyncMutex.Guard guard);

        private async UniTask<Result<ReaderBuffSegm<T>, RingBufferError>> RxAsync_
            ( NUsize offset
            , Demand demand
            , FnResetSegm resetSegm
            , CancellationToken token)
        {
            var log = Logger.Shared;
            if (!Demand.IsLegal(demand))
                throw new ArgumentException(message: $"Illegal demand ({demand})", paramName: nameof(demand));

            var demandMin = demand.Floor.SomeOrDefault(Demand.MinFloor) + offset;
            var demandMax = demand.Ceiling.SomeOrDefault(this.Capacity) + offset;

            if (demandMin > this.capacity_)
                return Result.Err(RingBufferError.Incapable);

            bool isSucc = false;
            var optGuard = await this.readerMutex_.AcquireAsync(token);
            if (!optGuard.IsSome(out var guard))
                return Result.Err(RingBufferError.Cancelled);
            try
            {
                while (true)
                {
                    var state = this.state_.LoadState();
                    var positions = AtomicState.LoadPositions(state: state, capacity: this.capacity_);

                    log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(RxAsync_)}] state({AtomicState.GetDebugInfo(state)}), demand({demand}), positions({positions})");

                    if (positions.R0.Length == 0 && AtomicState.ExpectRxCloseFlagged(state))
                        return Result.Err(RingBufferError.Closed);

                    var readerSize = positions.GetReadersSumLength();
                    if (readerSize >= demandMin)
                    {
                        var r0Len = positions.R0.Length;
                        log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(RxAsync_)}] readerSize({readerSize}), r0.Offset({positions.R0.Offset}), r0.Length({r0Len})");

                        if (offset >= r0Len)
                        {
                            demandMax -= r0Len;
                            var l0 = NUsize.Min(positions.R1.Length, demandMax);
                            this.readerMem_[0] = this.memory_.Slice(positions.R1.Offset + offset - r0Len, l0);
                            this.readerMem_[1] = ReadOnlyMemory<T>.Empty;
                        }
                        else
                        {
                            // 已知 offset 小于第一段可读缓冲区的长度，所以 positions.R0.Length - offset 有非负结果，
                            // 同时也意味着 positions.R0.Offset + offset <= this.Capacity
                            var l0 = NUsize.Min(positions.R0.Length, demandMax);
                            this.readerMem_[0] = this.memory_.Slice(offset + positions.R0.Offset, l0);

                            var restMin = demandMin - l0;
                            var restMax = demandMax - l0;
                            if (positions.Rc > 1 && positions.R1.Length >= restMin)
                            {
                                var l1 = NUsize.Min(positions.R1.Length, restMax);
                                this.readerMem_[1] = this.memory_.Slice(positions.R1.Offset, l1);
                            }
                            else
                                this.readerMem_[1] = ReadOnlyMemory<T>.Empty;
                        }
                        ReadOnlyMemory<ReadOnlyMemory<T>> m;
                        if (this.readerMem_[1].IsEmpty)
                            m = this.readerMem_.Slice(offset: NUsize.Zero, length: 1);
                        else
                            m = this.readerMem_;

                        log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(RxAsync_)}] m(Length: {m.Length}), cont: {m.DebugStr(a => a.EnumerateSpan())}");

                        var s = resetSegm(m, guard);
                        isSucc = true;
                        return Result.Ok(s);
                    }
                    var shouldWakeWriter = false;
                    if (!this.state_.SpinSetRxAwaitFlagged(token).IsSucc(out state))
                    {
                        if (token.IsCancellationRequested)
                        {
                            log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(RxAsync_)}] exit loop due to cancel");
                            return Result.Err(RingBufferError.Cancelled);
                        }
                        if (AtomicState.ExpectTxAwaitFlagged(state) && positions.GetWriterSumLength() > NUsize.Zero)
                        {
                            var x = this.state_.SpinSetRxAwaitFlagged_IgnoreTx(token);
                            if (!x.IsSucc(out state))
                                throw new Exception($"[{nameof(RingBuffer<T>)}.{nameof(RxAsync_)}] failed in setting RX_AWAIT flagged even igoring TX, state({AtomicState.GetDebugInfo(state)}) ");
                            shouldWakeWriter = true;
                        }
                        else
                        {
                            var m = $"[{nameof(RingBuffer<T>)}.{nameof(RxAsync_)}] failed in setting RX_AWAIT flagged, state({AtomicState.GetDebugInfo(state)})";
                            throw new Exception(m);
                        }
                    }
                    if (this.readerSema_.CurrentCount != 0)
                        throw new Exception($"Illegal state: readerDemand.CurrentCount={readerSema_.CurrentCount}");

                    this.ResetDemand_(ref this.pendingRxDemand_, Option.Some(demand));

                    log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(RxAsync_)}] rx await, state({AtomicState.GetDebugInfo(state)}), demand({demand}), positions({positions})");
                    var waitTask = this.readerSema_.WaitAsync(token);

                    if (shouldWakeWriter)
                    {
                        log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(RxAsync_)}] re-wake writer by manually reclaim reader mem offset(0)");
                        this.reclaimReaderBuffSegm_.Reclaim(this.readerMem_, NUsize.Zero);
                    }
                    await waitTask;
                    log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(RxAsync_)}] end rx await");

                    this.ResetDemand_(ref this.pendingRxDemand_, Option.None);
                }
            }
            catch (OperationCanceledException)
            {
                var x = this.state_.SpinSetRxAwaitUnflagged();
                log.Warn($"[{nameof(RingBuffer<T>)}.{nameof(RxAsync_)}] exit loop by cancelling task, state: {AtomicState.GetDebugInfo(x.Data)}");
                return Result.Err(RingBufferError.Cancelled);
            }
            catch (Exception e)
            {
                log.Error($"[{nameof(RingBuffer<T>)}.{nameof(RxAsync_)}] unexpected exception: {e}");
                throw;
            }
            finally
            {
                this.ResetDemand_(ref this.pendingRxDemand_, Option.None);
                this.pendingRxDemand_ = Option.None;
                if (!isSucc)
                    guard.Dispose();
            }
        }

        /// <summary>
        /// 从内部缓冲区中借出一段未填充缓存，该缓存长度不大于给定的长度；如果成功则返回该未填充缓存
        /// </summary>
        public async UniTask<Result<WriterBuffSegm<T>, RingBufferError>> WriteAsync
            ( Demand demand
            , CancellationToken token = default)
        {
            var log = Logger.Shared;
            bool isSucc = false;
            var optGuard = await this.writerMutex_.AcquireAsync(token);
            if (!optGuard.IsSome(out var guard))
                return Result.Err(RingBufferError.Cancelled);
            try
            {
                var demandMin = demand.Floor.SomeOrDefault(Demand.MinFloor);
                var demandMax = demand.Ceiling.SomeOrDefault(this.Capacity);
                while (true)
                {
                    var state = this.state_.LoadState();
                    var positions = AtomicState.LoadPositions(state: state, capacity: this.capacity_);

                    log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(WriteAsync)}] state({AtomicState.GetDebugInfo(state)}), demand({demand}), positions({positions})");

                    if (positions.W0.Length == 0 && AtomicState.ExpectTxCloseFlagged(state))
                        return Result.Err(RingBufferError.Closed);

                    var writerSize = positions.GetWriterSumLength();
                    if (writerSize >= demandMin)
                    {
                        var l0 = NUsize.Min(positions.W0.Length, demandMax);
                        this.writerMem_[0] = this.memory_.Slice(positions.W0.Offset, l0);
                        var rest = NUsize.Min(writerSize, demandMax) - l0;
                        if (positions.Wc > 1 && rest > 0)
                        {
                            var l1 = NUsize.Min(positions.W1.Length, rest);
                            this.writerMem_[1] = this.memory_.Slice(positions.W1.Offset, l1);
                        }
                        else
                            this.writerMem_[1] = Memory<T>.Empty;

                        ReadOnlyMemory<Memory<T>> m;
                        if (this.writerMem_[1].IsEmpty)
                            m = this.writerMem_.Slice(offset: 0, length: 1);
                        else
                            m = this.writerMem_;

                        log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(WriteAsync)}] m(Length: {m.Length}), cont: {m.DebugStr(a => a.EnumerateSpan())}");

                        this.writerSegm_.Reset(m, Option.Some(guard), this.reclaimWriterBuffSegm_);
                        isSucc = true;
                        return Result.Ok(this.writerSegm_);
                    }

                    var shouldWakeReader = false;
                    if (!this.state_.SpinSetTxAwaitFlagged(token).IsSucc(out state))
                    {
                        if (token.IsCancellationRequested)
                        {
                            log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(WriteAsync)}] exit loop due to cancel.");
                            return Result.Err(RingBufferError.Cancelled);
                        }
                        if (AtomicState.ExpectRxAwaitFlagged(state) && positions.GetReadersSumLength() > NUsize.Zero)
                        {
                            var x = this.state_.SpinSetTxAwaitFlagged_IgnoreRx(token);
                            if (!x.IsSucc(out state))
                                throw new Exception($"[{nameof(RingBuffer<T>)}.{nameof(RxAsync_)}] failed in setting TX_AWAIT flagged even igoring RX, state({AtomicState.GetDebugInfo(state)}) ");
                            shouldWakeReader = true;
                        }
                        else
                        {
                            var m = $"[{nameof(RingBuffer<T>)}.{nameof(WriteAsync)}] failed in setting TX_AWAIT flagged, state({AtomicState.GetDebugInfo(state)})";
                            throw new Exception(m);
                        }
                    }
                    if (this.writerSema_.CurrentCount != 0)
                        throw new Exception($"Illegal state, writerDemand.CurrentCount={this.writerSema_.CurrentCount}");

                    this.ResetDemand_(ref this.pendingTxDemand_, Option.Some(demand));

                    log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(WriteAsync)}] tx await, state({AtomicState.GetDebugInfo(state)}), demand({demand}), positions({positions})");
                    var waitTask = this.writerSema_.WaitAsync(token);
                    if (shouldWakeReader)
                    {
                        log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(WriteAsync)}] re-wake reader by manually reclaim writer mem offset(0)");
                        this.reclaimWriterBuffSegm_.Reclaim(this.writerMem_, NUsize.Zero);
                    }
                    await waitTask;
                    log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(WriteAsync)}] end tx await");

                    this.ResetDemand_(ref this.pendingTxDemand_, Option.None);
                }
            }
            catch (OperationCanceledException)
            {
                var x = this.state_.SpinSetTxAwaitUnflagged();
                log.Warn($"[{nameof(RingBuffer<T>)}.{nameof(WriteAsync)}] exit loop by cancelling task, state: {AtomicState.GetDebugInfo(x.Data)}");
                return Result.Err(RingBufferError.Cancelled);
            }
            catch (Exception e)
            {
                log.Error($"[{nameof(RingBuffer<T>)}.{nameof(WriteAsync)}] unexpected exception: {e}");
                throw;
            }
            finally
            {
                this.state_.SpinSetTxAwaitUnflagged();
                this.ResetDemand_(ref this.pendingTxDemand_, Option.None);
                if (!isSucc)
                    guard.Dispose();
            }
        }

        public bool IsRxClosed
        {
            get
            {
                var state = this.state_.LoadState();
                var positions = AtomicState.LoadPositions(state: state, capacity: this.capacity_);
                return positions.R0.Length == 0 && AtomicState.ExpectRxCloseFlagged(state);
            }
        }

        public bool IsTxClosed
        {
            get
            {
                var state = this.state_.LoadState();
                var positions = AtomicState.LoadPositions(state: state, capacity: this.capacity_);
                return positions.W0.Length == 0 && AtomicState.ExpectTxCloseFlagged(state);
            }
        }

        async UniTask<Result<ReaderBuffSegm<T>, IIoError>> IBuffRx<T>.ReadAsync(Demand demand, CancellationToken token)
        {
            var r = await this.ReadAsync(demand, token);
            return r.MapErr((e) => e as IIoError);
        }

        async UniTask<Result<ReaderBuffSegm<T>, IIoError>> IBuffRx<T>.PeekAsync(NUsize offset, Demand demand, CancellationToken token)
        {
            var r = await this.PeekAsync(offset, demand, token);
            return r.MapErr((e) => e as IIoError);
        }

        async UniTask<Result<WriterBuffSegm<T>, IIoError>> IBuffTx<T>.WriteAsync(Demand demand, CancellationToken token)
        {
            var r = await this.WriteAsync(demand, token);
            return r.MapErr((e) => e as IIoError);
        }

        private static void MarkRxClosed(IBuffRx<T> buffer)
        {
            var log = Logger.Shared;
            if (buffer is not RingBuffer<T> ringBuff)
                return;
            var r = ringBuff.state_.SpinSetRxClosed();
            if (!r.IsSucc(out var s))
            {
                log.Warn($"[{nameof(RingBuffer<T>)}.{nameof(MarkRxClosed)}] failed! state: {ringBuff.state_}");
                return;
            }
            if (ringBuff.writerSema_.CurrentCount == 0)
                ringBuff.writerSema_.Release();
        }

        private static void MarkTxClosed(IBuffTx<T> buffer)
        {
            var log = Logger.Shared;
            if (buffer is not RingBuffer<T> ringBuff)
                return;
            var r = ringBuff.state_.SpinSetTxClosed();
            if (!r.IsSucc(out var s))
            {
                log.Warn($"[{nameof(RingBuffer<T>)}.{nameof(MarkTxClosed)}] state: {s:x16}");
                return;
            }
            if (ringBuff.readerSema_.CurrentCount == 0)
                ringBuff.readerSema_.Release();
        }

        private Option<Demand> ResetDemand_
            ( ref Option<Demand> slot
            , Option<Demand> optDemand
            , CancellationToken token = default)
        {
            var res = this.state_.SpinAcquireIoMutex(token);
            if (!res.IsOk(out var ioMutex))
                return Option.None;
            Option<Demand> prevDemand = default;
            using (ioMutex)
            {
                prevDemand = slot;
                slot = optDemand;
            }
            return prevDemand;
        }

        private Option<Demand> PeekDemand_
            ( ref Option<Demand> slot
            , CancellationToken token = default)
        {
            var res = this.state_.SpinAcquireIoMutex(token);
            if (!res.IsOk(out var ioMutex))
                return Option.None;
            using (ioMutex)
            {
                return slot;
            }
        }

        private void ReclaimReaderBuffSegm_(NUsize offset)
        {
            var log = Logger.Shared;
            try
            {
                if (!offset.TryInto(out uint u32Count))
                    throw new Exception($"Failed to cast offset({offset}) into uint");

                var r = this.state_.SpinFwdReaderPos(u32Count, this.capacity_);
                ulong s0;
                if (!r.TryOk(out s0, out s0))
                    throw new Exception($"Failed fwd reader pos");

                var optPendingTxDemand = this.PeekDemand_(ref this.pendingTxDemand_);
                if (!optPendingTxDemand.IsSome(out var pendingTxDemand) && AtomicState.ExpectTxAwaitUnflagged(s0))
                {
                    log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(ReclaimReaderBuffSegm_)}] no pending tx demand, {AtomicState.GetDebugInfo(s0)}");
                    return;
                }
                var demandMin = pendingTxDemand.Floor.SomeOrDefault(NUsize.Zero);
                var p = AtomicState.LoadPositions(s0, this.capacity_);
                var available = p.GetWriterSumLength() + offset;
                if (available < demandMin)
                {
                    log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(ReclaimReaderBuffSegm_)}] insufficient(available({available}) < demandMin({demandMin})), positions: {p}");
                    return;
                }
                if (this.state_.SpinSetTxAwaitUnflagged().IsSucc(out s0))
                {
                    var wc = this.writerSema_.CurrentCount;
                    if (wc != 0)
                        throw new InvalidOperationException($"[{nameof(RingBuffer<T>)}.{nameof(ReclaimReaderBuffSegm_)}] TxAwait flagged with writerDemand_.CurrentCount: {wc}");
                    this.writerSema_.Release();
                }
            }
            catch (Exception e)
            {
                log.Error($"[{nameof(RingBuffer<T>)}.{nameof(ReclaimReaderBuffSegm_)}] offset({offset}), unexpected exception: {e}");
                throw;
            }
        }

        private void ReclaimWriterBuffSegm_(NUsize offset)
        {
            var log = Logger.Shared;
            ulong s0;
            try
            {
                if (!offset.TryInto(out uint u32Count))
                    throw new Exception($"Failed to cast offset({offset}) into uint");

                var r = this.state_.SpinFwdWriterPos(u32Count, this.capacity_);
                if (!r.TryOk(out s0, out s0))
                    throw new Exception($"[{nameof(RingBuffer<T>)}.{nameof(ReclaimWriterBuffSegm_)}] failed fwd writer pos");

                var optPendingRxDemand = this.PeekDemand_(ref this.pendingRxDemand_);
                if (!optPendingRxDemand.IsSome(out var pendingRxDemand) && AtomicState.ExpectRxAwaitUnflagged(s0))
                {
                    log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(ReclaimWriterBuffSegm_)}] no pending rx demand, {AtomicState.GetDebugInfo(s0)}");
                    return;
                }
                var demandMin = pendingRxDemand.Floor.SomeOrDefault(NUsize.Zero);
                var p = AtomicState.LoadPositions(s0, this.capacity_);
                var available = p.GetReadersSumLength() + offset;
                if (available < demandMin)
                {
                    log.Debug($"[{nameof(RingBuffer<T>)}.{nameof(ReclaimWriterBuffSegm_)}] insufficient(available({available}) < demandMin({demandMin})), positions: {p}");
                    return;
                }
                if (this.state_.SpinSetRxAwaitUnflagged().IsSucc(out s0))
                {
                    var rc = this.readerSema_.CurrentCount;
                    if (rc != 0)
                        throw new InvalidOperationException($"[{nameof(RingBuffer<T>)}.{nameof(ReclaimWriterBuffSegm_)}] RxAwait flagged with readerDemand_.CurrentCount: {rc}");
                    this.readerSema_.Release();
                }
            }
            catch (Exception e)
            {
                log.Error($"[{nameof(RingBuffer<T>)}.{nameof(ReclaimWriterBuffSegm_)}] offset({offset}), unexpected exception: {e}");
                throw;
            }
        }
    }

    public sealed partial class RingBuffer<T>
    {
        private readonly struct IoPosition
        {
            public readonly uint Offset;
            public readonly uint Length;

            public IoPosition(uint offset, uint length)
            {
                this.Offset = offset;
                this.Length = length;
            }

            public override string ToString()
                => $"(Offset: {this.Offset}, Length: {this.Length})";
        }

        private readonly struct StatePositions
        {
            public readonly IoPosition R0;

            public readonly IoPosition R1;

            public readonly IoPosition W0;

            public readonly IoPosition W1;

            /// <summary>
            /// 读者位置记录数，即 R1 是否存在有效数据
            /// </summary>
            public readonly uint Rc;

            /// <summary>
            /// 写者位置记录数，即 W1 是否存在有效数据
            /// </summary>
            public readonly uint Wc;

            public StatePositions(IoPosition r0, IoPosition r1, uint rc, IoPosition w0, IoPosition w1, uint wc)
            {
                this.R0 = r0;
                this.R1 = r1;
                this.Rc = rc;
                this.W0 = w0;
                this.W1 = w1;
                this.Wc = wc;
            }

            public readonly NUsize GetReadersSumLength()
            {
                if (this.Rc == 0)
                    return NUsize.Zero;
                if (this.Rc == 1)
                    return this.R0.Length;
                else
                    return this.R0.Length + this.R1.Length;
            }

            public readonly NUsize GetWriterSumLength()
            {
                if (this.Wc == 0)
                    return NUsize.Zero;
                if (this.Wc == 1)
                    return this.W0.Length;
                else
                    return this.W0.Length + this.W1.Length;
            }

            public override string ToString()
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"(r0: {this.R0}");
                if (this.Rc == 2)
                    sb.Append($", r1: {this.R1}");
                sb.Append($", w0: {this.W0}");
                if (this.Wc == 2)
                    sb.Append($", w1: {this.W1}");
                sb.Append(")");
                return sb.ToString();
            }
        }

        private sealed class AtomicState
        {
            public readonly struct IoMutexGuard : IDisposable
            {
                private readonly AtomicState atomicState_;

                internal IoMutexGuard(AtomicState atomicState)
                    => this.atomicState_ = atomicState;

                public void Dispose()
                {
                    var log = Logger.Shared;
                    var r = this.atomicState_.flags_.TrySpinCompareExchange(
                        ExpectIoMutexFlagged,
                        DesireIoMutexUnflagged
                    );
                    if (r.IsSucc(out var s))
                        return;
                    log.Error($"[{nameof(IoMutexGuard)}.{nameof(Dispose)}] release failed, state({s:x16})");
                }
            }

            private AtomicU64Flags flags_;

            public AtomicState(ulong initState = 0uL)
                => this.flags_ = new AtomicU64Flags(initState);

            #region Flags constants

            private const ulong K01_B63_IO_MUTEX_FLAG = 1uL << 63;

            private const ulong K01_B62_INVERSED_FLAG = 1uL << 62;

            private const ulong K01_B61_TX_AWAIT_FLAG = 1uL << 61;

            private const ulong K01_B60_RX_AWAIT_FLAG = 1uL << 60;

            private const ulong K01_B59_TX_CLOSE_FLAG = 1uL << 59;

            private const ulong K01_B58_RX_CLOSE_FLAG = 1uL << 58;

            private const ulong K31_B00_READER_POS_MASK = POSITION_MAX;

            private const ulong K31_B28_WRITER_POS_MASK = ((ulong)POSITION_MAX) << POS_BITS_WIDTH;

            private const int POS_BITS_WIDTH = 28;

            public const uint POSITION_MAX = (1u << POS_BITS_WIDTH) - 1;

            public const uint POSITION_ERR = 0xFEEE_EEEE;

            #endregion

            #region ReaderPosition

            public static uint GetReaderPosition(ulong state)
                => unchecked((uint)(state & K31_B00_READER_POS_MASK));

            public static ulong SetReaderPosition(ulong state, uint position)
                => (state & (~K31_B00_READER_POS_MASK)) | ((ulong)position);

            #endregion

            #region WriterPosition

            public static uint GetWriterPosition(ulong state)
                => unchecked((uint)((state & K31_B28_WRITER_POS_MASK) >> POS_BITS_WIDTH));

            public static ulong SetWriterPosition(ulong state, uint position)
            {
                var u64position = (ulong)position;
                return (state & (~K31_B28_WRITER_POS_MASK)) | (u64position << POS_BITS_WIDTH);
            }

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

            #region TX Close Flag

            public static bool ExpectTxCloseFlagged(ulong state)
                => (state & K01_B59_TX_CLOSE_FLAG) == K01_B59_TX_CLOSE_FLAG;

            public static bool ExpectTxCloseUnflagged(ulong state)
                => !ExpectTxCloseFlagged(state);

            public static ulong DesireTxCloseFlagged(ulong state)
                => state | K01_B59_TX_CLOSE_FLAG;

            #endregion

            #region RX Close Flag

            public static bool ExpectRxCloseFlagged(ulong state)
                => (state & K01_B58_RX_CLOSE_FLAG) == K01_B58_RX_CLOSE_FLAG;

            public static bool ExpectRxCloseUnflagged(ulong state)
                => !ExpectRxCloseFlagged(state);

            public static ulong DesireRxCloseFlagged(ulong state)
                => state | K01_B58_RX_CLOSE_FLAG;

            #endregion

            public static bool ExpectBothRxTxAwaitUnflagged(ulong state)
                => ExpectRxAwaitUnflagged(state) && ExpectTxAwaitUnflagged(state);

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

            #region RX_AWAIT Flag

            public static bool ExpectRxAwaitFlagged(ulong state)
                => (state & K01_B60_RX_AWAIT_FLAG) == K01_B60_RX_AWAIT_FLAG;

            public static bool ExpectRxAwaitUnflagged(ulong state)
                => !ExpectRxAwaitFlagged(state);

            public static ulong DesireRxAwaitFlagged(ulong state)
                => state | K01_B60_RX_AWAIT_FLAG;

            public static ulong DesireRxAwaitUnflagged(ulong state)
                => state & (~K01_B60_RX_AWAIT_FLAG);

            #endregion

            public static StatePositions LoadPositions(ulong state, uint capacity)
            {
                var st = state;
                var rp = GetReaderPosition(st);
                var wp = GetWriterPosition(st);

                if (ExpectInverseFlagged(st))
                {
                    var r0 = new IoPosition(offset: rp, length: capacity - rp);
                    var w0 = new IoPosition(offset: wp, length: rp - wp);
                    var w1 = default(IoPosition);
                    if (wp > 0)
                    {
                        var r1 = new IoPosition(offset: 0, length: wp);
                        return new StatePositions(r0, r1, rc: 2, w0, w1, wc: 1);
                    }
                    else
                    {
                        var r1 = default(IoPosition);
                        return new StatePositions(r0, r1, rc: 1, w0, w1, wc: 1);
                    }
                }
                else
                {
                    var r0 = new IoPosition(offset: rp, length: wp - rp);
                    var r1 = default(IoPosition);
                    var w0 = new IoPosition(offset: wp, length: capacity - wp);
                    if (rp > 0)
                    {
                        var w1 = new IoPosition(offset: 0, length: rp);
                        return new StatePositions(r0, r1, rc: 1, w0, w1, wc: 2);
                    }
                    else
                    {
                        var w1 = default(IoPosition);
                        return new StatePositions(r0, r1, rc: 1, w0, w1, wc: 1);
                    }
                }
            }

            public static string GetDebugInfo(ulong state)
            {
                // var isMutexAcq = AtomicState.ExpectIoMutexFlagged(state);
                // var isInverse = AtomicState.ExpectInverseFlagged(state);
                // var isTxAwait = AtomicState.ExpectTxAwaitFlagged(state);
                // var isRxAwait = AtomicState.ExpectRxAwaitFlagged(state);
                // var isTxClose = AtomicState.ExpectTxCloseFlagged(state);
                // var isRxClose = AtomicState.ExpectRxCloseFlagged(state);
                uint s = unchecked((uint)(state >> 60));
                return
                    $"({state:X16}: Mut Inv TxA RxA: {s:b4})";
                    // $"{state:X16}(MutexAcq: {isMutexAcq}, Inversed: {isInverse}, TxAwait: {isTxAwait}, RxAwait: {isRxAwait}, TxClose: {isTxClose}, RxClose: {isRxClose})";
            }

            public ulong LoadState()
                => this.flags_.Read();

            public Result<IoMutexGuard, ulong> SpinAcquireIoMutex(CancellationToken token = default)
            {
                var r = this.flags_.TrySpinCompareExchange(
                    ExpectIoMutexUnflagged,
                    DesireIoMutexFlagged
                );
                if (r.IsSucc(out var s))
                    return Result.Ok(new IoMutexGuard(this));
                else
                    return Result.Err(s);
            }

            public CmpXchResult<ulong> SpinSetTxClosed(CancellationToken token = default)
            {
                return this.flags_.TrySpinCompareExchange(
                    ExpectTxCloseUnflagged,
                    DesireTxCloseFlagged,
                    token
                );
            }

            public CmpXchResult<ulong> SpinSetRxClosed(CancellationToken token = default)
            {
                return this.flags_.TrySpinCompareExchange(
                    ExpectRxCloseUnflagged,
                    DesireRxCloseFlagged,
                    token
                );
            }

            public CmpXchResult<ulong> SpinSetTxAwaitUnflagged(CancellationToken token = default)
            {
                return this.flags_.TrySpinCompareExchange(
                    s => ExpectTxAwaitFlagged(s),
                    DesireTxAwaitUnflagged,
                    token
                );
            }

            public CmpXchResult<ulong> SpinSetTxAwaitFlagged(CancellationToken token = default)
            {
                return this.flags_.TrySpinCompareExchange(
                    ExpectBothRxTxAwaitUnflagged,
                    DesireTxAwaitFlagged
                );
            }

            public CmpXchResult<ulong> SpinSetTxAwaitFlagged_IgnoreRx(CancellationToken token = default)
            {
                return this.flags_.TrySpinCompareExchange(
                    ExpectTxAwaitUnflagged,
                    DesireTxAwaitFlagged
                );
            }

            public CmpXchResult<ulong> SpinSetRxAwaitUnflagged(CancellationToken token = default)
            {
                return this.flags_.TrySpinCompareExchange(
                    s => ExpectRxAwaitFlagged(s),
                    DesireRxAwaitUnflagged,
                    token
                );
            }

            public CmpXchResult<ulong> SpinSetRxAwaitFlagged(CancellationToken token = default)
            {
                return this.flags_.TrySpinCompareExchange(
                    ExpectBothRxTxAwaitUnflagged,
                    DesireRxAwaitFlagged
                );
            }

            public CmpXchResult<ulong> SpinSetRxAwaitFlagged_IgnoreTx(CancellationToken token = default)
            {
                return this.flags_.TrySpinCompareExchange(
                    ExpectRxAwaitUnflagged,
                    DesireRxAwaitFlagged
                );
            }


            /// <summary>
            /// Increase the writer position by spinning loop.
            /// </summary>
            public Result<ulong, ulong> SpinFwdWriterPos
                ( uint count
                , uint capacity
                , CancellationToken token = default)
            {
                var state = this.flags_.Read();
                Result<ulong, ulong> ret;

                while (true)
                {
                    var wp0 = AtomicState.GetWriterPosition(state);
                    var rp = AtomicState.GetReaderPosition(state);
                    var wpt = wp0 + count;
                    var shouldFlagInverse = wpt >= capacity;
                    if (shouldFlagInverse && AtomicState.ExpectInverseFlagged(state))
                        throw new InvalidOperationException($"s({state:x16}), wp({wp0}) + count({count}) >= capacity({capacity}) while inverse is flagged");

                    var wp1 = wpt % capacity;
                    if (wp1 > rp && shouldFlagInverse)
                        throw new InvalidOperationException($"s({state:x16}), (wp({wp0}) + count({count}) = {wpt}) % capacity({capacity}) > rp({rp}) while inverse is flagged");
                    var expected = state;
                    ulong desired;
                    {
                        var s = state;
                        if (shouldFlagInverse)
                            s = AtomicState.DesireInverseFlagged(s);
                        s = AtomicState.SetWriterPosition(s, wp1);
                        desired = s;
                    }
                    ret = this.flags_.CompareExchange(expected, desired);
                    if (ret.IsOk(out state) || token.IsCancellationRequested)
                        break;
                }
                return ret;
            }

            /// <summary>
            /// Increase the reader position by spinning loop.
            /// </summary>
            public Result<ulong, ulong> SpinFwdReaderPos
                ( uint count
                , uint capacity
                , CancellationToken token = default)
            {
                var state = this.flags_.Read();
                Result<ulong, ulong> ret;

                while (true)
                {
                    var rp0 = AtomicState.GetReaderPosition(state);
                    var wp = AtomicState.GetWriterPosition(state);
                    var rpt = rp0 + count;
                    var shouldUnflagInverse = rpt >= capacity;
                    if (shouldUnflagInverse && AtomicState.ExpectInverseUnflagged(state))
                        throw new InvalidOperationException($"s({state:x16}), rp({rp0}) + count({count}) >= capacity({capacity}) while inverse is unflagged");

                    var rp1 = rpt % capacity;
                    if (rp1 > wp && shouldUnflagInverse)
                        throw new InvalidOperationException($"s({state:x16}), (rp({rp0}) + count({count}) = {rpt}) % capacity({capacity}) > wp({wp}) while inverse is flagged");

                    var expected = state;
                    ulong desired;
                    {
                        var s = state;
                        if (shouldUnflagInverse)
                            s = AtomicState.DesireInverseUnflagged(s);
                        s = AtomicState.SetReaderPosition(s, rp1);
                        desired = s;
                    }
                    ret = this.flags_.CompareExchange(expected, desired);
                    if (ret.IsOk(out var replaced))
                        break;
                    if (token.IsCancellationRequested)
                        break;
                    state = replaced;
                }
                return ret;
            }
        }

        private sealed class ReclaimWriterBuffSegm : IReclaimMutableMemory<T>
        {
            private readonly RingBuffer<T> buffer_;

            public ReclaimWriterBuffSegm(RingBuffer<T> buffer)
                => this.buffer_ = buffer;

            public void Reclaim(ReadOnlyMemory<Memory<T>> mem, NUsize offset)
                => this.buffer_.ReclaimWriterBuffSegm_(offset);
        }

        /// <summary>
        /// 调整 readerOffset, 唤醒 writerDemand, 释放 readerSema
        /// </summary>
        private sealed class ReclaimReaderBuffSegm : IReclaimReadOnlyMemory<T>
        {
            private readonly RingBuffer<T> buffer_;

            public ReclaimReaderBuffSegm(RingBuffer<T> buffer)
                => this.buffer_ = buffer;

            public void Reclaim(ReadOnlyMemory<ReadOnlyMemory<T>> mem, NUsize offset)
                => this.buffer_.ReclaimReaderBuffSegm_(offset);
        }

        /// <summary>
        /// 只释放 readerSema
        /// </summary>
        private sealed class ReclaimPeekerBuffSegm : IReclaimReadOnlyMemory<T>
        {
            public ReclaimPeekerBuffSegm()
            {}

            public void Reclaim(ReadOnlyMemory<ReadOnlyMemory<T>> mem, NUsize offset)
            {}
        }
    }

    public static class TaskAttachExternalTokenExtensions
    {
        public static async Task AttachExternalCancellation(this Task task, CancellationToken token)
        {
            if (task.IsCompletedSuccessfully)
                return;
            if (token.IsCancellationRequested)
                throw new OperationCanceledException(token);

            var tcs = new TaskCompletionSource<int>();
            await using (token.Register(() => tcs.TrySetCanceled()))
            {
                if (await Task.WhenAny(task, tcs.Task) == tcs.Task)
                    throw new OperationCanceledException(token);
            }
            await task; 
        }

        public static async Task<T> AttachExternalCancellation<T>(this Task<T> task, CancellationToken token)
        {
            if (task.IsCompletedSuccessfully)
                return task.Result;
            if (token.IsCancellationRequested)
                throw new OperationCanceledException(token);

            var tcs = new TaskCompletionSource<int>();
            await using (token.Register(() => tcs.TrySetCanceled()))
            {
                if (await Task.WhenAny(task, tcs.Task) == tcs.Task)
                    throw new OperationCanceledException(token);
            }
            return await task; 
        }
    }

    public static class RingBufferCachedProxyExtensions
    {
        public static TxProxy<T> GetCachedTxProxy<T>(this RingBuffer<T> ringBuffer, Action<RingBuffer<T>>? closeOnDispose = null)
            => ringBuffer.GetCachedTxProxy<RingBuffer<T>, T>(closeOnDispose);

        public static RxProxy<T> GetCachedRxProxy<T>(this RingBuffer<T> ringBuffer, Action<RingBuffer<T>>? closeOnDispose = null)
            => ringBuffer.GetCachedRxProxy<RingBuffer<T>, T>(closeOnDispose);
    }
}
