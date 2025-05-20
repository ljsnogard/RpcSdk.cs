namespace BufferKit
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using LoggingSdk;

    public sealed class WriterBuffSegm<T>: IDisposable
    {
        private readonly AsyncMutex mutex_;

        private ReadOnlyMemory<Memory<T>> segments_;

        private Option<AsyncMutex.Guard> optGuard_;

        private IReclaimMutableMemory<T> reclaim_;

        private NUsize capacity_;

        private int currSegmIndex_;

        private int currSegmOffset_;

        private static readonly WriterBuffSegm<T> sharedEmpty_ = new WriterBuffSegm<T>();

        public static WriterBuffSegm<T> Empty
            => WriterBuffSegm<T>.sharedEmpty_;

        public WriterBuffSegm
            ( ReadOnlyMemory<Memory<T>> segments
            , Option<AsyncMutex.Guard> optGuard
            , IReclaimMutableMemory<T> reclaim
            , int currSegmIndex
            , int currSegmOffset)
        {
            this.mutex_ = new AsyncMutex();
            this.segments_ = segments;
            this.optGuard_ = optGuard;
            this.reclaim_ = reclaim;
            this.capacity_ = segments.GetSumCapacity();
            this.currSegmIndex_ = currSegmIndex;
            this.currSegmOffset_ = currSegmOffset;
        }

        private WriterBuffSegm() : this
            ( segments: ReadOnlyMemory<Memory<T>>.Empty
            , optGuard: Option.None
            , reclaim: NoReclaim<T>.Shared
            , currSegmIndex: 0
            , currSegmOffset: 0)
        {}

        internal void Reset
            ( ReadOnlyMemory<Memory<T>> segments
            , Option<AsyncMutex.Guard> optGuard
            , IReclaimMutableMemory<T> reclaim)
        {
            this.segments_ = segments;
            this.optGuard_ = optGuard;
            this.reclaim_ = reclaim;
            this.capacity_ = segments.GetSumCapacity();
            this.currSegmIndex_ = 0;
            this.currSegmOffset_ = 0;
        }

        internal AsyncMutex Mutex
            => this.mutex_;

        /// <summary>
        /// 缓冲区的最大容量
        /// </summary>
        public NUsize Capacity
            => this.capacity_;

        /// <summary>
        /// 未填充的缓冲区总长度
        /// </summary>
        public NUsize Length
            => this.segments_.GetLength(this.currSegmIndex_, this.currSegmOffset_);

        /// <summary>
        /// 已填充的缓冲区长度
        /// </summary>
        public NUsize Offset
            => this.segments_.GetOffset(this.currSegmIndex_, this.currSegmOffset_);

        public ReadOnlyMemory<Memory<T>> GetUnwrittenSlices()
            => this.segments_.GetUnconsumedSlices(this.currSegmIndex_, this.currSegmOffset_);

        public NUsize Forward(NUsize length)
            => this.segments_.Forward(length, ref this.currSegmIndex_, ref currSegmOffset_);

        private void Dispose_(bool isDisposing)
        {
            if (this.segments_.IsEmpty)
                return;
            if (isDisposing)
            {
                try
                {
                    // Logger.Shared.Debug($"[{nameof(ReaderBuffSegm<T>)}.{nameof(Dispose_)}] readerSegm({this.GetHashCodeStrX8()}) reclaim, offset({this.offset_})");
                    this.reclaim_.Reclaim(this.segments_, this.Offset);
                    if (this.optGuard_.IsSome(out var guard))
                        guard.Dispose();
                }
                catch (Exception e)
                {
                    Logger.Shared.Error($"[{nameof(WriterBuffSegm<T>)}.{nameof(Dispose_)}] unexpected exception: {e}");
                    throw;
                }
                finally
                {
                    this.optGuard_ = Option.None;
                    this.segments_ = ReadOnlyMemory<Memory<T>>.Empty;
                }
            }
            else
                Logger.Shared.Warn($"[{nameof(WriterBuffSegm<T>)}.{nameof(Dispose_)}]");
        }

        public void Dispose()
            => this.Dispose_(true);

        ~WriterBuffSegm()
            => this.Dispose_(false);
    }

    public static class WriterBuffSegmExtensions
    {
        public static UniTask<Result<NUsize, BuffSegmError>> DumpAsync<T>(this WriterBuffSegm<T> target, ReaderBuffSegm<T> source, CancellationToken token = default)
            => source.FillAsync(target, token);

        public static async UniTask<Result<NUsize, BuffSegmError>> DumpAsync<T>
            ( this WriterBuffSegm<T> target
            , ReadOnlyMemory<T> source
            , CancellationToken token = default)
        {
            var optGuard = await target.Mutex.AcquireAsync(token);
            try
            {
                if (!optGuard.IsSome(out var guard))
                    throw new OperationCanceledException(token);

                var unwrittenSlices = target.GetUnwrittenSlices();
                var dstIndex = 0;
                var dstOffset = 0;
                var srcOffset = 0;
                while (true)
                {
                    if (dstIndex >= unwrittenSlices.Length || srcOffset >= source.Length)
                        break;
                    var src = source.Slice(start: srcOffset);
                    var dst = unwrittenSlices.Span[dstIndex].Slice(start: dstOffset);
                    var len = Math.Min(src.Length, dst.Length);
                    src.Slice(0, len).CopyTo(dst.Slice(0, len));
                    srcOffset += len;
                    dstOffset += len;

                    if (dstOffset == dst.Length)
                    {
                        dstIndex++;
                        dstOffset = 0;
                    }
                }
                var res = (NUsize)srcOffset;
                target.Forward(res);
                return Result.Ok(res);
            }
            catch (OperationCanceledException)
            {
                return Result.Ok(NUsize.Zero);
            }
            catch (Exception e)
            {
                Logger.Shared.Error($"[{nameof(WriterBuffSegmExtensions)}.{nameof(DumpAsync)}`{nameof(ReadOnlyMemory<T>)}] unexpected exception: {e}");
                throw;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }
    }

    public static class WriterBuffSegmEnumerateSpanExtensions
    {
        public static IEnumerable<Memory<T>> EnumerateSpan<T>(this WriterBuffSegm<T> s)
        {
            var memArr = s.GetUnwrittenSlices();
            for (var i = 0; i < memArr.Length; ++i)
            {
                var a = memArr.Span[i];
                if (a.IsEmpty)
                    yield break;
                yield return a;
            }
        }
    }
}