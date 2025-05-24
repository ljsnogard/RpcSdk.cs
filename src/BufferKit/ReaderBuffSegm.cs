namespace NsBufferKit
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using NsAnyLR;

    using LoggingSdk;

    /// <summary>
    /// 从 `RingBuffer` 借出的用于读取数据的缓冲区段落
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public sealed partial class ReaderBuffSegm<T> : IDisposable
    {
        private readonly AsyncMutex mutex_;

        private ReadOnlyMemory<ReadOnlyMemory<T>> segments_;

        private Option<AsyncMutex.Guard> optGuard_;

        private IReclaimReadOnlyMemory<T> reclaim_;

        private NUsize capacity_;

        private int currSegmIndex_;

        private int currSegmOffset_;

        private static readonly ReaderBuffSegm<T> sharedEmpty_ = new ReaderBuffSegm<T>();

        public static ReaderBuffSegm<T> Empty
            => ReaderBuffSegm<T>.sharedEmpty_;

        public ReaderBuffSegm
            ( ReadOnlyMemory<ReadOnlyMemory<T>> segments
            , Option<AsyncMutex.Guard> optGuard
            , IReclaimReadOnlyMemory<T> reclaim
            , int currSegmIndex
            , int currSegmOffset)
        {
            this.mutex_ = new();
            this.segments_ = segments;
            this.optGuard_ = optGuard;
            this.reclaim_ = reclaim;
            this.capacity_ = segments.GetSumCapacity();
            this.currSegmIndex_ = currSegmIndex;
            this.currSegmOffset_ = currSegmOffset;
        }

        private ReaderBuffSegm() : this
            ( segments: ReadOnlyMemory<ReadOnlyMemory<T>>.Empty
            , optGuard: Option.None()
            , reclaim: NoReclaim<T>.Shared
            , currSegmIndex: 0
            , currSegmOffset: 0)
        {}

        internal void Reset
            ( ReadOnlyMemory<ReadOnlyMemory<T>> segments
            , Option<AsyncMutex.Guard> optGuard
            , IReclaimReadOnlyMemory<T> reclaim)
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

        public NUsize Capacity
            => this.capacity_;

        public NUsize Length
            => this.segments_.GetLength(this.currSegmIndex_, this.currSegmOffset_);

        public NUsize Offset
            => this.segments_.GetOffset(this.currSegmIndex_, this.currSegmOffset_);

        public ReadOnlyMemory<ReadOnlyMemory<T>> GetUnreadSlices()
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
                    Logger.Shared.Error($"[{nameof(ReaderBuffSegm<T>)}.{nameof(Dispose_)}] unexpected exception: {e}");
                    throw;
                }
                finally
                {
                    this.optGuard_ = Option.None();
                    this.segments_ = ReadOnlyMemory<ReadOnlyMemory<T>>.Empty;
                }
            }
            else
                Logger.Shared.Warn($"[{nameof(ReaderBuffSegm<T>)}.{nameof(Dispose_)}]");
        }

        public void Dispose()
            => this.Dispose_(true);

        ~ReaderBuffSegm()
            => this.Dispose_(false);
    }

    public static class ReaderSegmBuffExtensions
    {
        public static async UniTask<Result<NUsize, BuffSegmError>> FillAsync<T>
            ( this ReaderBuffSegm<T> source
            , WriterBuffSegm<T> target
            , CancellationToken token = default)
        {
            var optTargetGuard = await target.Mutex.AcquireAsync(token);
            NUsize fillLen = NUsize.Zero;
            try
            {
                if (!optTargetGuard.IsSome(out var targetGuard))
                    throw new OperationCanceledException(token);

                var targetData = target.GetUnwrittenSlices();
                var dstIndex = 0;
                var dstOffset = 0;
                while (true)
                {
                    if (dstIndex >= targetData.Length || source.Length == 0)
                        break;
                    var dst = targetData.Span[dstIndex];
                    var res = await source.FillAsync(dst, token);
                    if (!res.IsOk(out var itemsCopied))
                        return res;
                    if (!itemsCopied.TryInto(out int u32ItemsCopied))
                        throw new Exception();
                    fillLen += itemsCopied;
                    dstOffset += u32ItemsCopied;
                    if (dstOffset == dst.Length)
                    {
                        dstIndex++;
                        dstOffset = 0;
                    }
                }
                source.Forward(fillLen);
                target.Forward(fillLen);
                return Result.Ok(fillLen);
            }
            catch (OperationCanceledException)
            {
                return Result.Ok(fillLen);
            }
            catch (Exception e)
            {
                Logger.Shared.Error($"[{nameof(ReaderSegmBuffExtensions)}.{nameof(FillAsync)}`{nameof(WriterBuffSegm<T>)}] unexpected exception: {e}");
                throw;
            }
            finally
            {
                if (optTargetGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        public static async UniTask<Result<NUsize, BuffSegmError>> FillAsync<T>(
            this ReaderBuffSegm<T> source,
            Memory<T> target,
            CancellationToken token = default)
        {
            var optGuard = await source.Mutex.AcquireAsync(token);
            try
            {
                if (!optGuard.IsSome(out var guard))
                    throw new OperationCanceledException(token);

                var unreadPart = source.GetUnreadSlices();
                var srcIndex = 0;
                var srcOffset = 0;
                var dstOffset = 0;
                while (true)
                {
                    if (srcIndex >= unreadPart.Length || dstOffset >= target.Length)
                        break;
                    var src = unreadPart.Span[srcIndex].Slice(start: srcOffset);
                    var dst = target.Slice(start: dstOffset);
                    var len = Math.Min(src.Length, dst.Length);
                    src.Slice(0, len).CopyTo(dst.Slice(0, len));
                    srcOffset += len;
                    dstOffset += len;
                    if (srcOffset == src.Length)
                    {
                        srcIndex++;
                        srcOffset = 0;
                    }
                }
                var ret = (NUsize)dstOffset;
                source.Forward(ret);
                return Result.Ok(ret);
            }
            catch (OperationCanceledException)
            {
                return Result.Ok(NUsize.Zero);
            }
            catch (Exception e)
            {
                Logger.Shared.Error($"[{nameof(ReaderSegmBuffExtensions)}.{nameof(FillAsync)}`{nameof(Memory<T>)}] unexpected exception: {e}");
                throw;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }
    }

    public static class ReaderBuffSegmEnumerateSpanExtensions
    {
        public static IEnumerable<ReadOnlyMemory<T>> EnumerateSpan<T>(this ReaderBuffSegm<T> s)
        {
            var memArr = s.GetUnreadSlices();
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
