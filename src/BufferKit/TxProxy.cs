namespace BufferKit
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using LoggingSdk;

    /// <summary>
    /// 缓冲区的生产者端代理，用于实现多种多线程环境下的由基本API组合完成的操作
    /// </summary>
    /// <typeparam name="T">缓冲区的数据单元类型</typeparam>
    public sealed class TxProxy<T> : IBuffTx<T>, IDisposable
    {
        private readonly IBuffTx<T> buffTx_;

        private readonly Action<IBuffTx<T>> closeOnDispose_;

        private readonly AsyncMutex taskMutex_;

        private bool isDisposed_;

        private TxProxy(IBuffTx<T> buffer, Action<IBuffTx<T>> closeOnDispose)
        {
            this.buffTx_ = buffer;
            this.closeOnDispose_ = closeOnDispose;
            this.taskMutex_ = new();
            this.isDisposed_ = false;
        }

        private static void DoNothingWithTx(IBuffTx<T> tx)
        { }

        private static void InvokeRxDispose(IBuffTx<T> tx)
            => ((IDisposable)tx).Dispose();

        private static readonly TxProxy<T> empty_ = new TxProxy<T>(new EmptyBuffer<T>(), DoNothingWithTx);

        public static TxProxy<T> Empty
            => TxProxy<T>.empty_;

        public static TxProxy<T> CreateProxy<R>(R tx)
            where R : class, IBuffTx<T>
        {
            if (tx is EmptyBuffer<T> empty)
                return TxProxy<T>.Empty;
            if (tx is TxProxy<T> self)
                return self;
            if (tx is IDisposable disposableRx)
                return new(tx, InvokeRxDispose);
            else
                return new(tx, DoNothingWithTx);
        }

        public static TxProxy<T> CreateProxy<R>(R tx, Action<R> closeOnDispose) 
            where R : class, IBuffTx<T>
        {
            void WrappedCloseOnDispose_(IBuffTx<T> tx)
            {
                if (tx is R concreteRx)
                    closeOnDispose(concreteRx);
                else
                    throw new Exception($"[{nameof(TxProxy<T>)}.{nameof(CreateProxy)}`({typeof(R).FullName}, {typeof(Action<R>).Name}).{nameof(WrappedCloseOnDispose_)}] expecting type ({typeof(R).FullName}) but ({tx.GetType().FullName}) encountered");
            }
            return new(tx, WrappedCloseOnDispose_);
        }

        public NUsize Capacity
        {
            get
            {
                if (!this.isDisposed_)
                    return this.buffTx_.Capacity;
                else
                    throw this.CreateObjectDisposedException();
            }
        }

        /// <summary>
        /// 调用所代理目标的同签名方法 <seealso cref="IBuffTx.WriteAsync(Demand, CancellationToken)"/>
        /// </summary>
        public UniTask<Result<WriterBuffSegm<T>, IIoError>> WriteAsync
            ( Demand demand
            , CancellationToken token = default)
        {
            if (!this.isDisposed_)
                return this.buffTx_.WriteAsync(demand, token);
            else
                throw CreateObjectDisposedException();
        }

        private void Dispose_(bool isDisposing)
        {
            if (this.isDisposed_)
                return; 
            if (isDisposing)
            {
                try
                {
                    this.closeOnDispose_(this.buffTx_);
                }
                finally
                {
                    this.isDisposed_ = true;
                    GC.SuppressFinalize(this);
                }
            }
            else
                Logger.Shared.Warn($"[{nameof(TxProxy<T>)}.{nameof(Dispose_)}]");
        }

        public void Dispose()
            => this.Dispose_(true);

        ~TxProxy()
            => this.Dispose_(false);

        static ObjectDisposedException CreateObjectDisposedException()
            => new ObjectDisposedException(typeof(TxProxy<T>).ToString());

        public UniTask<Result<NUsize, IIoError>> DumpAsync(RxProxy<T> source, CancellationToken token = default)
            => this.DumpAsync(source, Option.None, token);

        public UniTask<Result<NUsize, IIoError>> DumpAsync(ReaderBuffSegm<T> source, CancellationToken token = default)
            => this.DumpAsync(source, Option.None, token);

        public UniTask<Result<NUsize, IIoError>> DumpAsync(ReadOnlyMemory<T> source, CancellationToken token = default)
            => this.DumpAsync(source, Option.None, token);

        /// <summary>
        /// 从一个消费端中取出数据作为源数据写入
        /// </summary>
        public async UniTask<Result<NUsize, IIoError>> DumpAsync
            ( RxProxy<T> source
            , Option<AsyncMutex.Guard> optTaskGuard
            , CancellationToken token = default)
        {
            if (this.isDisposed_)
                throw this.CreateObjectDisposedException();

            using var ensured = await optTaskGuard.EnsureGuardedAsync(this.taskMutex_, token);
            try
            {
                NUsize sentCount = 0;
                optTaskGuard = await this.taskMutex_.AcquireAsync(token);
                if (optTaskGuard.IsNone())
                    return Result.Ok(sentCount);

                Logger.Shared.Debug($"[{nameof(TxProxy<T>)}.{nameof(DumpAsync)}`{nameof(RxProxy<T>)}] Tx({this.buffTx_.GetHashCodeStrX8()}) starts loop");
                while (true)
                {
                    // Logger.Shared.Debug($"[{nameof(TxProxy<T>)}.{nameof(DumpAsync)}`{nameof(RxProxy<T>)}] Tx({this.buffTx_.GetHashCodeStrX8()}) before tx.WriteAsync({writeLen}) txSize({txSize})");
                    var writeRes = await this.WriteAsync(Demand.Least, token);
                    if (!writeRes.TryOk(out var dstSegm, out var dstErr))
                    {
                        if (sentCount > 0)
                            return Result.Ok(sentCount);
                        else
                            return Result.Err(dstErr);
                    }
                    try
                    {
                        var fillRes = await source.FillAsync(dstSegm, Option.None, token);
                        if (!fillRes.TryOk(out var copyCount, out var fillErr))
                        {
                            if (sentCount > 0)
                                return Result.Ok(sentCount);
                            else
                                return Result.Err(dstErr);
                        }
                        sentCount += copyCount;
                    }
                    finally
                    {
                        dstSegm.Dispose();
                    }
                }
            }
            finally
            { }
        }

        /// <summary>
        /// 将参数所指定的外部缓存中的数据以复制的方式填充到内部缓存中，并返回已复制的长度
        /// </summary>
        public async UniTask<Result<NUsize, IIoError>> DumpAsync
            ( ReaderBuffSegm<T> source
            , Option<AsyncMutex.Guard> optTaskGuard
            , CancellationToken token = default)
        {
            if (this.isDisposed_)
                throw this.CreateObjectDisposedException();

            using var ensured = await optTaskGuard.EnsureGuardedAsync(this.taskMutex_, token);
            try
            {
                NUsize dumpCount = 0;
                NUsize srcLength = source.Length;
                while (dumpCount < srcLength)
                {
                    var writeLen = srcLength - dumpCount;
                    var writeRes = await this.WriteAsync(Demand.AtMost(writeLen), token);
                    if (!writeRes.TryOk(out var dstSegm, out var txErr))
                    {
                        if (dumpCount > 0)
                            return Result.Ok(dumpCount);
                        else
                            return Result.Err(txErr);
                    }
                    var dataSegms = source.GetUnreadSlices();
                    try
                    {
                        for (var i = 0; i < dataSegms.Length; ++i)
                        {
                            var src = dataSegms.Span[i];
                            var dumpRes = await this.DumpAsync(src, Option.Some(ensured.Guard), token);
                            if (!dumpRes.TryOk(out var copyCount, out var dumpErr))
                                return Result.Err(dumpErr);
                            dumpCount += copyCount;
                        }
                    }
                    finally
                    {
                        source.Forward(dumpCount);
                    }
                }
                return Result.Ok(dumpCount);
            }
            finally
            { }
        }

        public async UniTask<Result<NUsize, IIoError>> DumpAsync
            ( ReadOnlyMemory<T> source
            , Option<AsyncMutex.Guard> optTaskGuard
            , CancellationToken token = default)
        {
            if (this.isDisposed_)
                throw this.CreateObjectDisposedException();

            using var ensured = await optTaskGuard.EnsureGuardedAsync(this.taskMutex_, token);
            try
            {
                var filledCount = NUsize.Zero;
                var sourceLength = source.NUsizeLength();
                while (filledCount < sourceLength)
                {
                    var unfilledLength = sourceLength - filledCount;
                    var writeRes = await this.buffTx_.WriteAsync(Demand.AtMost(unfilledLength), token);
                    if (!writeRes.TryOk(out var writerSegm, out var writerErr))
                    {
                        if (filledCount > 0)
                            break;
                        else
                            return Result.Err(writerErr);
                    }
                    using (writerSegm)
                    {
                        var dumpRes = await writerSegm.DumpAsync(source.Slice(offset: filledCount), token);
                        if (!dumpRes.TryOk(out var copyCount, out var dumpErr))
                        {
                            if (filledCount > 0)
                                break;
                            else
                                return Result.Err<IIoError>(dumpErr);
                        }
                        filledCount += copyCount;
                    }
                }
                return Result.Ok(filledCount);
            }
            catch (Exception e)
            {
                Logger.Shared.Error($"[{nameof(TxProxy<T>)}.{nameof(DumpAsync)}`{nameof(ReadOnlyMemory<T>)}] err: {e}");
                throw;
            }
            finally
            { }
        }

        public UniTask<Result<T, IIoError>> EnqueueAsync
            ( Func<T> factory
            , CancellationToken token = default)
        {
            return this.EnqueueAsync(factory, Option.None, token);
        }

        public async UniTask<Result<T, IIoError>> EnqueueAsync
            ( Func<T> factory
            , Option<AsyncMutex.Guard> optTaskGuard
            , CancellationToken token = default)
        {
            var log = Logger.Shared;
            if (this.isDisposed_)
                throw this.CreateObjectDisposedException();

            var ensured = await optTaskGuard.EnsureGuardedAsync(this.taskMutex_, token);
            try
            {
                var writeRes = await this.buffTx_.WriteAsync(Demand.AtMost(1), token);
                if (!writeRes.TryOk(out var writerSegm, out var ioErr))
                    return Result.Err(ioErr);

                using (writerSegm)
                {
                    if (writerSegm.Length != 1)
                        throw new Exception($"[{nameof(IBuffTx<T>)}.{nameof(EnqueueAsync)}] writerSEgm.Length={writerSegm.Length}");

                    var slices = writerSegm.GetUnwrittenSlices();
                    var mem = slices.Span[0];
                    if (mem.Length != 1)
                        throw new Exception($"[{nameof(IBuffTx<T>)}.{nameof(EnqueueAsync)}] mem.Length={mem.Length}");

                    mem.Span[0] = factory();
                    writerSegm.Forward(1);
                    return Result.Ok(mem.Span[0]);
                }
            }
            catch (Exception e)
            {
                log.Error($"[{nameof(TxProxy<T>)}.{nameof(EnqueueAsync)}] unexpected exception: {e}");
                throw;
            }
            finally
            {
                ensured.Dispose();
            }
        }

        public async UniTask<Result<NUsize, IIoError>> EnqueueAsync
            ( T item
            , CancellationToken token = default)
        {
            if (this.isDisposed_)
                throw this.CreateObjectDisposedException();

            var r = await this.EnqueueAsync(() => item, token);
            return r.MapOk(_ => (NUsize)1);
        }
    }

    public static class TxGetCachedProxyExtensions
    {
        private static class Cached<T>
        {
            private static readonly Lazy<ConcurrentDictionary<IBuffTx<T>, TxProxy<T>>> lazyTxDict_ = new(() => new());

            public static ConcurrentDictionary<IBuffTx<T>, TxProxy<T>> ConcurrentTxCache
                => lazyTxDict_.Value;

            public static void RemoveOnDispose(IBuffTx<T> tx)
                => ConcurrentTxCache.TryRemove(tx, out _);
        }

        public static TxProxy<T> GetCachedTxProxy<B, T>(this B buffTx, Action<B>? closeOnDispose = null)
            where B : class, IBuffTx<T>
        {
            void WrappedCloseOnDispose_(IBuffTx<T> tx)
            {
                Cached<T>.RemoveOnDispose(tx);
                if (closeOnDispose is Action<B> closeTx)
                    closeTx((B)tx);
            }
            if (buffTx is TxProxy<T> proxy)
                return proxy;
            return Cached<T>
                .ConcurrentTxCache
                .GetOrAdd(buffTx, k => TxProxy<T>.CreateProxy(k, WrappedCloseOnDispose_));
        }
    }
}