namespace NsBufferKit
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using NsAnyLR;

    using LoggingSdk;

    public sealed class OutputProxy<T> : IUnbufferedOutput<T>, IDisposable
    {
        private readonly IUnbufferedOutput<T> output_;

        private readonly AsyncMutex taskMutex_;

        private readonly Action<IUnbufferedOutput<T>> closeOnDispose_;

        private bool isDisposed_;

        private OutputProxy
            ( IUnbufferedOutput<T> input
            , Action<IUnbufferedOutput<T>> closeOnDispose)
        {
            this.output_ = input;
            this.taskMutex_ = new();
            this.closeOnDispose_ = closeOnDispose;
            this.isDisposed_ = false;
        }

        private static void DoNothingWithOutput(IUnbufferedOutput<T> input)
        { }

        private static void InvokeOutputDispose(IUnbufferedOutput<T> output)
            => ((IDisposable)output).Dispose();

        public static OutputProxy<T> CreateProxy<O>(O output)
            where O : class, IUnbufferedOutput<T>
        {
            if (output is IDisposable disposable)
                return new(output, InvokeOutputDispose);
            else
                return new(output, DoNothingWithOutput);
        }

        public static OutputProxy<T> CreateProxy<O>
            ( O output
            , Action<O> closeOnDispose
            )
            where O : class, IUnbufferedOutput<T>
        {
            void WrappedCloseOnDispose_(IUnbufferedOutput<T> output)
            {
                if (output is O concreteOutput)
                    closeOnDispose(concreteOutput);
                else
                    throw new Exception($"[{nameof(OutputProxy<T>)}.{nameof(CreateProxy)}`({typeof(O).FullName}, {typeof(Action<O>).Name}).{nameof(WrappedCloseOnDispose_)}] expecting type ({typeof(O).FullName}) but ({output.GetType().FullName}) encountered");
            }
            return new(output, WrappedCloseOnDispose_);
        }

        public UniTask<Result<NUsize, IIoError>> WriteAsync(ReadOnlyMemory<T> source, CancellationToken token = default) 
            => this.WriteAsync(source, Option.None(), token);

        public UniTask<Result<NUsize, IIoError>> WriteAsync(ReaderBuffSegm<T> source, CancellationToken token = default)
            => this.WriteAsync(source, Option.None(), token);

        public async UniTask<Result<NUsize, IIoError>> WriteAsync
            ( ReaderBuffSegm<T> source
            , Option<AsyncMutex.Guard> optTaskGuard = default
            , CancellationToken token = default)
        {
            if (this.isDisposed_)
                throw this.CreateObjectDisposedException();

            using var ensured = await optTaskGuard.EnsureGuardedAsync(this.taskMutex_, token);
            var writtenCount = NUsize.Zero;
            var segmData = source.GetUnreadSlices();
            for (var i = 0; i < segmData.Length; ++i)
            {
                var mem = segmData.Span[i];
                var writeRes = await this.WriteAsync(mem, Option.Some(ensured.Guard), token);
                if (!writeRes.TryOk(out var cpCount, out var writeErr))
                {
                    if (writtenCount > 0)
                        break;
                    else
                        return Result.Err(writeErr);
                }
                writtenCount += cpCount;
                if (writtenCount == source.Length)
                    break;
            }
            return Result.Ok(writtenCount);
        }

        public async UniTask<Result<NUsize, IIoError>> WriteAsync
            ( ReadOnlyMemory<T> source
            , Option<AsyncMutex.Guard> optTaskGuard = default
            , CancellationToken token = default)
        {
            if (this.isDisposed_)
                throw this.CreateObjectDisposedException();
            using var ensured = await optTaskGuard.EnsureGuardedAsync(this.taskMutex_, token);
            var writtenCount = NUsize.Zero;
            try
            {
                while (writtenCount < source.NUsizeLength())
                {
                    var src = source.Slice(offset: writtenCount);
                    var writeRes = await this.output_.WriteAsync(src, token);
                    if (!writeRes.TryOk(out var cpCount, out var writeErr))
                    {
                        if (writtenCount > 0)
                            break;
                        else
                            return Result.Err(writeErr);
                    }
                    writtenCount += cpCount;
                }
                return Result.Ok(writtenCount);
            }
            catch (OperationCanceledException)
            {
                return Result.Ok(writtenCount);
            }
            catch (Exception e)
            {
                Logger.Shared.Error($"[{nameof(OutputProxy<T>)}.{nameof(WriteAsync)}`{nameof(ReadOnlyMemory<T>)}] unexpected exception: {e}");
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
                    this.closeOnDispose_(this.output_);
                }
                finally
                {
                    this.isDisposed_ = true;
                    GC.SuppressFinalize(this);
                }
            }
            else
                Logger.Shared.Warn($"[{nameof(OutputProxy<T>)}.{nameof(Dispose_)}]");
        }

        ~OutputProxy()
            => this.Dispose_(false);
    }

    public static class OutputGetCachedProxyExtensions
    {
        private static class Cached<T>
        {
            private static readonly Lazy<ConcurrentDictionary<IUnbufferedOutput<T>, OutputProxy<T>>> lazyDict_ = new(() => new());

            public static ConcurrentDictionary<IUnbufferedOutput<T>, OutputProxy<T>> ConcurrentCache
                => lazyDict_.Value;

            public static void RemoveOnDispose(IUnbufferedOutput<T> output)
                => ConcurrentCache.TryRemove(output, out _);
        }

        public static OutputProxy<T> GetCachedProxy<O, T>
            ( this O output
            , Action<O>? closeOnDispose = null
            )
            where O : class, IUnbufferedOutput<T>
        {
            void WrappedCloseOnDispose_(IUnbufferedOutput<T> output)
            {
                Cached<T>.RemoveOnDispose(output);
                if (closeOnDispose is Action<O> closeOutput)
                    closeOutput((O)output);
            }
            if (output is OutputProxy<T> proxy)
                return proxy;
            return Cached<T>
                .ConcurrentCache
                .GetOrAdd(output, k => OutputProxy<T>.CreateProxy(k, WrappedCloseOnDispose_));
        }
    }
}