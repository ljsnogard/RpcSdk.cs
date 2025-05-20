namespace BufferKit
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using LoggingSdk;

    public sealed class InputProxy<T> : IUnbufferedInput<T>, IDisposable
    {
        private readonly IUnbufferedInput<T> input_;

        private readonly AsyncMutex taskMutex_;

        private readonly Action<IUnbufferedInput<T>> closeOnDispose_;

        private bool isDisposed_;

        private InputProxy
            ( IUnbufferedInput<T> input
            , Action<IUnbufferedInput<T>> closeOnDispose)
        {
            this.input_ = input;
            this.taskMutex_ = new();
            this.closeOnDispose_ = closeOnDispose;
            this.isDisposed_ = false;
        }

        private static void DoNothingWithInput(IUnbufferedInput<T> input)
        { }

        private static void InvokeInputDispose(IUnbufferedInput<T> input)
            => ((IDisposable)input).Dispose();

        public static InputProxy<T> CreateProxy<I>(I input)
            where I : class, IUnbufferedInput<T>
        {
            if (input is IDisposable disposable)
                return new(input, InvokeInputDispose);
            else
                return new(input, DoNothingWithInput);
        }

        public static InputProxy<T> CreateProxy<I>
            ( I input
            , Action<I> closeOnDispose
            )
            where I : class, IUnbufferedInput<T>
        {
            void WrappedCloseOnDispose_(IUnbufferedInput<T> input)
            {
                if (input is I concreteInput)
                    closeOnDispose(concreteInput);
                else
                    throw new Exception($"[{nameof(InputProxy<T>)}.{nameof(CreateProxy)}`({typeof(I).FullName}, {typeof(Action<I>).Name}).{nameof(WrappedCloseOnDispose_)}] expecting type ({typeof(I).FullName}) but ({input.GetType().FullName}) encountered");
            }
            return new(input, WrappedCloseOnDispose_);
        }

        public UniTask<Result<NUsize, IIoError>> ReadAsync(Memory<T> target, CancellationToken token = default) 
            => this.ReadAsync(target, Option.None, token);

        public UniTask<Result<NUsize, IIoError>> ReadAsync(WriterBuffSegm<T> segment, CancellationToken token = default)
            => this.ReadAsync(segment, Option.None, token);

        public async UniTask<Result<NUsize, IIoError>> ReadAsync
            ( WriterBuffSegm<T> target
            , Option<AsyncMutex.Guard> optTaskGuard
            , CancellationToken token = default)
        {
            if (this.isDisposed_)
                throw this.CreateObjectDisposedException();

            using var ensured = await optTaskGuard.EnsureGuardedAsync(this.taskMutex_, token);
            var readCount = NUsize.Zero;
            var segmData = target.GetUnwrittenSlices();
            for (var i = 0; i < segmData.Length; ++i)
            {
                var mem = segmData.Span[i];
                var readRes = await this.ReadAsync(mem, Option.Some(ensured.Guard), token);
                if (!readRes.TryOk(out var cpCount, out var readErr))
                {
                    if (readCount > 0)
                        break;
                    else
                        return Result.Err(readErr);
                }
                readCount += cpCount;
                if (readCount == target.Length)
                    break;
            }
            return Result.Ok(readCount);
        }

        public async UniTask<Result<NUsize, IIoError>> ReadAsync
            ( Memory<T> target
            , Option<AsyncMutex.Guard> optTaskGuard
            , CancellationToken token = default)
        {
            if (this.isDisposed_)
                throw this.CreateObjectDisposedException();
            using var ensured = await optTaskGuard.EnsureGuardedAsync(this.taskMutex_, token);
            var readCount = NUsize.Zero;
            try
            {
                while (readCount < target.NUsizeLength())
                {
                    var dst = target.Slice(offset: readCount);
                    var readRes = await this.input_.ReadAsync(dst, token);
                    if (!readRes.TryOk(out var cpCount, out var readErr))
                    {
                        if (readCount > 0)
                            break;
                        else
                            return Result.Err(readErr);
                    }
                    readCount += cpCount;
                }
                return Result.Ok(readCount);
            }
            catch (OperationCanceledException)
            {
                return Result.Ok(readCount);
            }
            catch (Exception e)
            {
                Logger.Shared.Error($"[{nameof(InputProxy<T>)}.{nameof(ReadAsync)}`{nameof(Memory<T>)}] unexpected exception: {e}");
                throw;
            }
        }

        public void Dispose()
            => this.Dispose_(true);

        private void Dispose_(bool isDisposing)
        {
            if  (this.isDisposed_)
                return;
            if (isDisposing)
            {
                try
                {
                    this.closeOnDispose_(this.input_);
                }
                finally
                {
                    this.isDisposed_ = true;
                    GC.SuppressFinalize(this);
                }
            }
            else
                Logger.Shared.Warn($"[{nameof(InputProxy<T>)}.{nameof(Dispose_)}]");
        }

        ~InputProxy()
            => this.Dispose_(false);
    }

    public static class InputGetCachedProxyExtensions
    {
        private static class Cached<T>
        {
            private static readonly Lazy<ConcurrentDictionary<IUnbufferedInput<T>, InputProxy<T>>> lazyDict_ = new(() => new());

            public static ConcurrentDictionary<IUnbufferedInput<T>, InputProxy<T>> ConcurrentCache
                => lazyDict_.Value;

            public static void RemoveOnDispose(IUnbufferedInput<T> input)
                => ConcurrentCache.TryRemove(input, out _);
        }

        public static InputProxy<T> GetCachedProxy<I, T>
            ( this I input
            , Action<I>? closeOnDispose = null
            )
            where I : class, IUnbufferedInput<T>
        {
            void WrappedCloseOnDispose_(IUnbufferedInput<T> input)
            {
                Cached<T>.RemoveOnDispose(input);
                if (closeOnDispose is Action<I> closeInput)
                    closeInput((I)input);
            }
            if (input is InputProxy<T> proxy)
                return proxy;
            return Cached<T>
                .ConcurrentCache
                .GetOrAdd(input, k => InputProxy<T>.CreateProxy(k, WrappedCloseOnDispose_));
        }
    }
}