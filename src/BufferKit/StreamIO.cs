namespace NsBufferKit
{
    using System;
    using System.IO;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using NsAnyLR;

    using LoggingSdk;

    public abstract class StreamIO
    {
        private readonly Stream stream_;

        private uint disposeFlags_;

        internal protected const uint K_INPUT_DISPOSE = 2u;

        internal protected const uint K_OUTPUT_DISPOSE = 3u;

        internal protected const uint K_DUAL_DISPOSE = K_INPUT_DISPOSE * K_OUTPUT_DISPOSE;

        internal protected StreamIO(Stream stream)
        {
            this.stream_ = stream;
            this.disposeFlags_ = 1u;
        }

        public static (StreamOutput, StreamInput) Split(Stream stream)
        {
            var output = new StreamOutput(stream);
            var input = new StreamInput(stream);
            return (output, input);
        }

        internal protected Stream Stream
            => this.stream_;

        internal protected void DisposeStream_(uint disposeFlag)
        {
            if (this.disposeFlags_ % disposeFlag == 0)
                return;
            try
            {
                this.disposeFlags_ *= disposeFlag;
            }
            finally
            {
                if (this.disposeFlags_ == K_DUAL_DISPOSE)
                {
                    this.stream_.Dispose();
                    GC.SuppressFinalize(this);
                }
            }
        }
    }

    public sealed class StreamInput : StreamIO, IUnbufferedInput<byte>, IDisposable
    {
        private readonly AsyncMutex mutex_;

        private bool isDisposed_;

        internal StreamInput(Stream stream) : base(stream)
        {
            this.mutex_ = new();
            this.isDisposed_ = false;
        }

        public async UniTask<Result<NUsize, IIoError>> ReadAsync(Memory<byte> target, CancellationToken token = default)
        {
            var log = Logger.Shared;
            Option<AsyncMutex.Guard> optGuard = Option.None();
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Result.Ok(NUsize.Zero);
                var c = await base.Stream.ReadAsync(target, token);
                if (NUsize.TryFrom(c).TryOk(out var readCount, out var err))
                    return Result.Ok(readCount);
                var m = $"Unable to parse read result {err} to NUsize";
                throw new Exception(m);
            }
            catch (OperationCanceledException)
            {
                return Result.Ok(NUsize.Zero);
            }
            catch (Exception e)
            {
                log.Error($"[{nameof(StreamInput)}.{nameof(ReadAsync)}] {e}");
                throw;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        #region IDisposable

        public void Dispose()
            => this.Dispose_(true);

        ~StreamInput()
            => this.Dispose_(false);

        private void Dispose_(bool isDisposing)
        {
            if (this.isDisposed_)
                return;
            if (isDisposing)
            {
                try
                {
                    base.DisposeStream_(K_INPUT_DISPOSE);
                }
                finally
                {
                    this.isDisposed_ = true;
                }
            }
            else
                Logger.Shared.Warn($"[{nameof(StreamInput)}.{nameof(Dispose_)}] isDisposing_ false");
        }

        #endregion
    }

    public sealed class StreamOutput : StreamIO, IUnbufferedOutput<byte>
    {
        private readonly AsyncMutex mutex_;

        private bool isDisposed_;

        internal StreamOutput(Stream stream) : base(stream)
        {
            this.mutex_ = new();
            this.isDisposed_ = false;
        }

        public async UniTask<Result<NUsize, IIoError>> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken token = default)
        {
            var log = Logger.Shared;
            Option<AsyncMutex.Guard> optGuard = Option.None();
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Result.Ok(NUsize.Zero);

                await base.Stream.WriteAsync(source, token);
                return Result.Ok(source.NUsizeLength());
            }
            catch (OperationCanceledException)
            {
                return Result.Ok(NUsize.Zero);
            }
            catch (Exception e)
            {
                log.Error($"[{nameof(StreamInput)}.{nameof(WriteAsync)}] {e}");
                throw;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        #region IDisposable

        public void Dispose()
            => this.Dispose_(true);

        ~StreamOutput()
            => this.Dispose_(false);

        private void Dispose_(bool isDisposing)
        {
            if (this.isDisposed_)
                return;
            if (isDisposing)
            {
                try
                {
                    base.DisposeStream_(K_OUTPUT_DISPOSE);
                }
                finally
                {
                    this.isDisposed_ = true;
                }
            }
            else
                Logger.Shared.Warn($"[{nameof(StreamOutput)}.{nameof(Dispose_)}] isDisposing_ false");
        }

        #endregion
    }

    public static class StreamInputGetCachedProxyExtensions
    {
        public static InputProxy<byte> GetCachedProxy(this StreamInput input)
            => input.GetCachedProxy<StreamInput, byte>();
    }

    public static class StreamOutputGetCachedProxyExtensions
    {
        public static OutputProxy<byte> GetCachedProxy(this StreamOutput output)
            => output.GetCachedProxy<StreamOutput, byte>();
    }
}
