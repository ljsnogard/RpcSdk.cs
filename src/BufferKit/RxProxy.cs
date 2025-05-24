namespace NsBufferKit
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using NsAnyLR;

    using LoggingSdk;

    /// <summary>
    /// 缓冲区的消费者端代理，用于实现多种多线程环境下的由基本API组合完成的操作
    /// </summary>
    /// <typeparam name="T">缓冲区的数据单元类型</typeparam>
    public sealed class RxProxy<T> : IBuffRx<T>, IDisposable
    {
        private readonly IBuffRx<T> buffRx_;

        private readonly Action<IBuffRx<T>> closeOnDispose_;

        private readonly AsyncMutex taskMutex_;

        private bool isDisposed_;

        private RxProxy
            ( IBuffRx<T> rx
            , Action<IBuffRx<T>> closeOnDispose)
        {
            this.buffRx_ = rx;
            this.closeOnDispose_ = closeOnDispose;
            this.taskMutex_ = new();
            this.isDisposed_ = false;
        }

        private static void DoNothingWithRx(IBuffRx<T> rx)
        { }

        private static void InvokeRxDispose(IBuffRx<T> rx)
            => ((IDisposable)rx).Dispose();

        private static readonly RxProxy<T> empty_ = new RxProxy<T>
            ( new EmptyBuffer<T>()
            , DoNothingWithRx);

        public static RxProxy<T> Empty
            => RxProxy<T>.empty_;

        public static RxProxy<T> CreateProxy<R>(R rx)
            where R : class, IBuffRx<T>
        {
            if (rx is EmptyBuffer<T> empty)
                return RxProxy<T>.Empty;
            if (rx is RxProxy<T> self)
                return self;
            if (rx is IDisposable disposableRx)
                return new(rx, InvokeRxDispose);
            else
                return new(rx, DoNothingWithRx);
        }

        public static RxProxy<T> CreateProxy<R>(R rx, Action<R> closeOnDispose)
            where R : class, IBuffRx<T>
        {
            void WrappedCloseOnDispose_(IBuffRx<T> rx)
            {
                if (rx is R concreteRx)
                    closeOnDispose(concreteRx);
                else
                    throw new Exception($"[{nameof(InputProxy<T>)}.{nameof(CreateProxy)}`({typeof(R).FullName}, {typeof(Action<R>).Name}).{nameof(WrappedCloseOnDispose_)}] expecting type ({typeof(R).FullName}) but ({rx.GetType().FullName}) encountered");
            }
            return new(rx, WrappedCloseOnDispose_);
        }

        public NUsize Capacity
        {
            get
            {
                if (!this.isDisposed_)
                    return this.buffRx_.Capacity;
                else
                    throw this.CreateObjectDisposedException();
            }
        }

        /// <summary>
        /// 从内部缓冲区中借出一段可供消费者读取的已填充缓存，该缓存长度不小于给定的长度；如果执行完成，则返回该已填充缓存
        /// </summary>
        /// <remarks>此方法内部不会尝试获取任务锁</remarks>
        public UniTask<Result<ReaderBuffSegm<T>, IIoError>> ReadAsync
            ( Demand demand
            , CancellationToken token = default)
        {
            if (!this.isDisposed_)
                return this.buffRx_.ReadAsync(demand, token);

            Logger.Shared.Error($"[{nameof(RxProxy<T>)}.{nameof(ReadAsync)}`{nameof(NUsize)}] RxProxy({this.GetHashCodeStrX8()}) is disposed");
            throw this.CreateObjectDisposedException();
        }

        /// <summary>
        /// 预览缓冲区中所有已填充的数据而不消费这些数据
        /// </summary>
        public UniTask<Result<ReaderBuffSegm<T>, IIoError>> PeekAsync
            ( NUsize offset
            , Demand demand
            , CancellationToken token = default)
        {
            if (!this.isDisposed_)
                return this.buffRx_.PeekAsync(offset, demand, token);

            Logger.Shared.Error($"[{nameof(RxProxy<T>)}.{nameof(PeekAsync)}`{nameof(NUsize)}] RxProxy({this.GetHashCodeStrX8()}) is disposed");
            throw this.CreateObjectDisposedException();
        }

        public UniTask<Result<NUsize, IIoError>> PeekAsync
            ( NUsize offset
            , Memory<T> buffer
            , CancellationToken toke = default)
        {
            throw new NotImplementedException();
        }

        private void Dispose_(bool isDisposing)
        {
            if (this.isDisposed_)
                return;
            if (isDisposing)
            {
                try
                {
                    this.closeOnDispose_(this.buffRx_);
                }
                finally
                {
                    this.isDisposed_ = true;
                    GC.SuppressFinalize(this);
                }
            }
            else
                Logger.Shared.Warn($"[{nameof(RxProxy<T>)}.{nameof(Dispose_)}]");
        }
        
        public void Dispose()
            => this.Dispose_(true);

        ~RxProxy()
            => this.Dispose_(false);

        public UniTask<Result<NUsize, IIoError>> FillAsync(WriterBuffSegm<T> target, CancellationToken token = default)
            => this.FillAsync(target, Option.None(), token);

        public UniTask<Result<NUsize, IIoError>> FillAsync(Memory<T> target, CancellationToken token = default)
            => this.FillAsync(target, Option.None(), token);

        public async UniTask<Result<NUsize, IIoError>> FillAsync
            ( WriterBuffSegm<T> target
            , Option<AsyncMutex.Guard> optTaskGuard
            , CancellationToken token = default)
        {
            if (this.isDisposed_)
                throw this.CreateObjectDisposedException();

            using var ensured = await optTaskGuard.EnsureGuardedAsync(this.taskMutex_, token);
            try
            {
                NUsize recvCount = 0;
                var writeLen = target.Length - recvCount;
                var srcSegmResult = await this.ReadAsync(Demand.AtMost(writeLen), token);
                if (!srcSegmResult.TryOk(out var srcSegm, out var readErr))
                {
                    if (recvCount > 0)
                        return Result.Ok(recvCount);
                    else
                        return Result.Err(readErr);
                }
                try
                {
                    var fillRes = await srcSegm.FillAsync(target, token);
                    if (!fillRes.TryOk(out var copyCount, out var fillErr))
                    {
                        if (recvCount > 0)
                            return Result.Ok(recvCount);
                        else
                            return Result.Err<IIoError>(fillErr);
                    }
                }
                finally
                {
                    srcSegm.Dispose();
                }
                return Result.Ok(recvCount);
            }
            finally
            { }
        }

        /// <summary>
        /// 将已缓存数据以复制的方式填充到参数指定的外部缓存，返回已复制的数据量并消耗相应长度。
        /// </summary>
        public async UniTask<Result<NUsize, IIoError>> FillAsync
            ( Memory<T> target
            , Option<AsyncMutex.Guard> optTaskGuard
            , CancellationToken token = default)
        {
            if (this.isDisposed_)
                throw this.CreateObjectDisposedException();

            using var ensured = await optTaskGuard.EnsureGuardedAsync(this.taskMutex_, token);
            try
            {
                var maybeTargetLen = NUsize.TryFrom(target.Length);
                if (!maybeTargetLen.TryOk(out var targetLength, out var inconvertibleLength))
                    throw new ArgumentException(message: $"Unable to convert length({inconvertibleLength}: {inconvertibleLength.GetType()}) to NUsize", paramName: nameof(target));

                var filledCount = NUsize.Zero;
                while (filledCount < targetLength)
                {
                    var unfilledLength = targetLength - filledCount;

                    // Logger.Shared.Debug($"[{nameof(RxProxy<T>)}.{nameof(FillAsync)}`{nameof(Memory<T>)}] TargetRx({this.buffRx_.GetHashCodeStrX8()}) before ReadAsync({unfilledLength}), filledCount({filledCount}), targetLength({targetLength})");

                    var maybeReaderSegment = await this.ReadAsync(Demand.Least, token);
                    if (!maybeReaderSegment.TryOk(out var readerSegm, out var readerErr))
                        return Result.Err(readerErr);

                    // Logger.Shared.Debug($"[{nameof(RxProxy<T>)}.{nameof(FillAsync)}`{nameof(Memory<T>)}] TargetRx({this.buffRx_.GetHashCodeStrX8()}) after  ReadAsync({unfilledLength}), filledCount({filledCount}), segments.Length({readerSegments.Length})");
                    try
                    {
                        var fillRes = await readerSegm.FillAsync(target, token);
                        if (!fillRes.IsOk(out var copyCount))
                            break;
                        filledCount += copyCount;
                    }
                    finally
                    {
                        readerSegm.Dispose();
                    }
                }
                return Result.Ok(filledCount);
            }
            finally
            { }
        }

        /// <summary>
        /// 忽略内容，直接消耗不大于指定长度的已填充数据，通常与 PeekAsync 搭配使用。
        /// </summary>
        public UniTask<Result<NUsize, IIoError>> SkipAsync
            ( NUsize length
            , CancellationToken token = default)
        {
            return this.SkipAsync(length, Option.None(), token);
        }

        public async UniTask<Result<NUsize, IIoError>> SkipAsync
            ( NUsize length
            , Option<AsyncMutex.Guard> optTaskGuard
            , CancellationToken token = default)
        {
            if (length >= this.Capacity)
                throw new ArgumentOutOfRangeException(paramName: nameof(length));
            var log = Logger.Shared;
            var ensured = await optTaskGuard.EnsureGuardedAsync(this.taskMutex_, token);
            try
            {
                var readRes = await this.ReadAsync(Demand.AtLeast(length), token);
                if (!readRes.TryOk(out var readSegm, out var readErr))
                    return Result.Err(readErr);
                if (readSegm.Length < length)
                    throw new Exception();
                try
                {
                    readSegm.Forward(length);
                    return Result.Ok(length);
                }
                finally
                {
                    readSegm.Dispose();
                }
            }
            catch (Exception e)
            {
                log.Error($"[{nameof(RxProxy<T>)}.{nameof(SkipAsync)}] exception: {e}");
                throw;
            }
            finally
            {
                ensured.Dispose();
            }
        }

        public async UniTask<Result<T, IIoError>> DequeueAsync(CancellationToken token = default)
        {
            if (this.isDisposed_)
                throw this.CreateObjectDisposedException();

            var log = Logger.Shared;
            var readRes = await this.ReadAsync(Demand.AtMost(1), token);
            if (!readRes.TryOk(out var readerSegm, out var ioErr))
                return Result.Err(ioErr);
            try
            {
                if (readerSegm.Length != 1)
                    throw new Exception($"[{nameof(RxProxy<T>)}.{nameof(DequeueAsync)}] readerSegm.Length={readerSegm.Length}");
                var slices = readerSegm.GetUnreadSlices();
                var mem = slices.Span[0];
                if (mem.Length != 1)
                    throw new Exception($"[{nameof(RxProxy<T>)}.{nameof(DequeueAsync)}] mem.Length={slices.Length}");

                var item = mem.Span[0];
                readerSegm.Forward(1);
                return Result.Ok(item);
            }
            catch (Exception e)
            {
                log.Error($"[{nameof(RxProxy<T>)}.{nameof(DequeueAsync)}] unexpected exception: {e}");
                throw;
            }
            finally
            {
                readerSegm.Dispose();
            }
        }

        /// <summary>
        /// 预览指定偏移量后的已缓存的数据，并复制到指定的内存区域中，返回已复制的数据量。此方法不会消耗任何数据。
        /// </summary>
        public async UniTask<Result<NUsize, IIoError>> PeekAsync
            ( NUsize offset
            , Memory<T> target
            , Option<AsyncMutex.Guard> optTaskGuard = default
            , CancellationToken token = default)
        {
            var maybeTargetLen = NUsize.TryFrom(target.Length);
            if (!maybeTargetLen.TryOk(out var targetLength, out var inconvertibleLength))
                throw new ArgumentException(message: $"Unable to convert length({inconvertibleLength}: {inconvertibleLength.GetType()}) to NUsize", paramName: nameof(target));

            using var ensured = await optTaskGuard.EnsureGuardedAsync(this.taskMutex_, token);
            try
            {
                NUsize filledCount = 0;
                while (filledCount < targetLength)
                {
                    var peekRes = await this.buffRx_.PeekAsync(offset + filledCount, Demand.Least, token);
                    if (!peekRes.TryOk(out var segm, out var readerErr))
                        return Result.Err(readerErr);
                    try
                    {
                        var fillRes = await segm.FillAsync(target, token);
                        if (!fillRes.IsOk(out var copyCount))
                            break;
                        filledCount += copyCount;
                    }
                    finally
                    {
                        segm.Dispose();
                    }
                }
                return Result.Ok(filledCount);
            }
            finally
            { }
        }
    }

    public static class RxGetCachedProxyExtensions
    {
        private static class Cached<T>
        {
            private static readonly Lazy<ConcurrentDictionary<IBuffRx<T>, RxProxy<T>>> lazyRxDict_ = new(() => new());

            public static ConcurrentDictionary<IBuffRx<T>, RxProxy<T>> ConcurrentRxCahce
                => lazyRxDict_.Value;

            public static void RemoveOnDispose(IBuffRx<T> rx)
                => ConcurrentRxCahce.TryRemove(rx, out _);
        }

        public static RxProxy<T> GetCachedRxProxy<B, T>(this B buffRx, Action<B>? closeOnDispose = null)
            where B : class, IBuffRx<T>
        {
            void WrappedCloseOnDispose_(IBuffRx<T> rx)
            {
                Cached<T>.RemoveOnDispose(rx);
                if (closeOnDispose is Action<B> closeRx)
                    closeRx((B)rx);
            }
            if (buffRx is RxProxy<T> proxy)
                return proxy;
            return Cached<T>
                .ConcurrentRxCahce
                .GetOrAdd(buffRx, k => RxProxy<T>.CreateProxy(k, WrappedCloseOnDispose_));
        }
    }
}