namespace NsBufferKit
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using NsAnyLR;

    using LoggingSdk;

    public interface IBuffRx<T>
    {
        public NUsize Capacity { get; }

        /// <summary>
        /// 从内部缓冲区中借出一段可供消费者读取的已填充缓存
        /// </summary>
        public UniTask<Result<ReaderBuffSegm<T>, IIoError>> ReadAsync
            ( Demand demand
            , CancellationToken token = default);

        /// <summary>
        /// 在不改变缓冲区读写位置的情况下预览缓冲区中的已填充的数据
        /// </summary>
        public UniTask<Result<ReaderBuffSegm<T>, IIoError>> PeekAsync
            ( NUsize offset
            , Demand demand
            , CancellationToken token = default);
    }

    /// <summary>
    /// 可支持且仅支持一对生产者和消费者的缓冲
    /// </summary>
    public interface IBuffTx<T>
    {
        public NUsize Capacity { get; }

        /// <summary>
        /// 从内部缓冲区中借出一段未填充缓存，该缓存长度不大于给定的长度；如果执行完成，则返回该未填充缓存
        /// </summary>
        public UniTask<Result<WriterBuffSegm<T>, IIoError>> WriteAsync
            ( Demand demand
            , CancellationToken token = default);
    }

    /// <summary>
    /// 可支持且仅支持一对生产者和消费者的缓冲
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IScspBuffer<T> : IBuffRx<T>, IBuffTx<T>
    {
        public bool IsRxClosed { get; }

        public bool IsTxClosed { get; }
    }

    public readonly struct EmptyBufferErr : IIoError
    {
        public Exception AsException()
            => new Exception(message: nameof(EmptyBufferErr));
    }

    public readonly struct EmptyBuffer<T> : IScspBuffer<T>
    {
        public NUsize Capacity
            => NUsize.Zero;

        public UniTask<Result<ReaderBuffSegm<T>, IIoError>> ReadAsync(Demand demand, CancellationToken token = default)
            => UniTask.FromResult<Result<ReaderBuffSegm<T>, IIoError>>(Result.Err<IIoError>(new EmptyBufferErr()));

        public UniTask<Result<ReaderBuffSegm<T>, IIoError>> PeekAsync(NUsize offset, Demand demand, CancellationToken token = default)
            => UniTask.FromResult<Result<ReaderBuffSegm<T>, IIoError>>(Result.Err<IIoError>(new EmptyBufferErr()));

        public UniTask<Result<WriterBuffSegm<T>, IIoError>> WriteAsync(Demand demand, CancellationToken token = default)
            => UniTask.FromResult<Result<WriterBuffSegm<T>, IIoError>>(Result.Err<IIoError>(new EmptyBufferErr()));

        public bool IsRxClosed
            => true;

        public bool IsTxClosed
            => true;
    }

    internal static class CreateObjectDisposedExceptionExt
    {
        public static ObjectDisposedException CreateObjectDisposedException<T>(this T obj, string objectName = "") where T : IDisposable
            => new ObjectDisposedException(objectName: objectName, message: $"Instance({obj.GetHashCodeStrX8()}) of type {obj.GetType().FullName} disposed.");
    }

    internal static class SegmentsCapacityExtensions
    {
        #region Capacity

        public static NUsize GetSumCapacity<S>
            ( in this ReadOnlyMemory<S> segments
            , Func<S, NUsize> getCapacity)
        {
            var capacity = NUsize.Zero;
            for (var i = 0; i < segments.Length; ++i)
            {
                var s = segments.Span[i];
                capacity += getCapacity(s);
            }
            return capacity;
        }

        public static NUsize GetSumCapacity<T>(this ReadOnlyMemory<Memory<T>> segments)
            => GetSumCapacity<Memory<T>>(in segments, s => (NUsize)s.Length);

        public static NUsize GetSumCapacity<T>(this ReadOnlyMemory<ReadOnlyMemory<T>> segments)
            => GetSumCapacity<ReadOnlyMemory<T>>(in segments, s => (NUsize)s.Length);

        #endregion

        #region Length

        public static NUsize GetLength<S>
            ( in this ReadOnlyMemory<S> segments
            , Func<S, int> getLength
            , int currSegmIndex
            , int currSegmOffset)
        {
                var sum = NUsize.Zero;
                for (var i = currSegmIndex; i < segments.Length; ++i)
                {
                    var s = segments.Span[i];
                    var l = getLength(s);
                    var len = i == currSegmIndex ? l - currSegmOffset : l;
                    sum += (NUsize)len;
                }
                return sum;
        }

        public static NUsize GetLength<T>
            ( this ReadOnlyMemory<ReadOnlyMemory<T>> segments
            , int currSegmIndex
            , int currSegmOffset)
        {
            return segments.GetLength(s => s.Length, currSegmIndex, currSegmOffset);
        }

        public static NUsize GetLength<T>
            ( this ReadOnlyMemory<Memory<T>> segments
            , int currSegmIndex
            , int currSegmOffset)
        {
            return segments.GetLength(s => s.Length, currSegmIndex, currSegmOffset);
        }

        #endregion

        #region Offset

        public static NUsize GetOffset<S>
            ( in this ReadOnlyMemory<S> segments
            , Func<S, int> getLength
            , int currSegmIndex
            , int currSegmOffset)
        {
            var sum = NUsize.Zero;
            for (var i = 0; i <= currSegmIndex; ++i)
            {
                if (i >= segments.Length)
                    break;
                var s = segments.Span[i];
                var o = i == currSegmIndex ? currSegmOffset : getLength(s);
                sum += (NUsize)o;
            }
            return sum;
        }

        public static NUsize GetOffset<T>
            ( this ReadOnlyMemory<ReadOnlyMemory<T>> segments
            , int currSegmIndex
            , int currSegmOffset)
        {
            return segments.GetOffset(s => s.Length, currSegmIndex, currSegmOffset);
        }

        public static NUsize GetOffset<T>
            ( this ReadOnlyMemory<Memory<T>> segments
            , int currSegmIndex
            , int currSegmOffset)
        {
            return segments.GetOffset(s => s.Length, currSegmIndex, currSegmOffset);
        }

        #endregion

        #region Forward

        public static NUsize Forward<S>
            ( in this ReadOnlyMemory<S> segments
            , Func<S, int> getLength
            , NUsize amount
            , ref int currSegmIndex
            , ref int currSegmOffset)
        {
            var rest = amount;
            while (currSegmIndex < segments.Length)
            {
                var segm = segments.Span[currSegmIndex];
                var size = (NUsize)(getLength(segm) - currSegmOffset);
                var decr = NUsize.Min(rest, size);
                if (size == decr)
                {
                    currSegmIndex ++;
                    currSegmOffset = 0;
                }
                else
                {
                    currSegmOffset += (int)decr;
                }
                rest -= decr;
                if (rest == NUsize.Zero)
                    break;
            }
            return segments.GetLength(getLength, currSegmIndex, currSegmOffset);
        }

        public static NUsize Forward<T>
            ( in this ReadOnlyMemory<ReadOnlyMemory<T>> segments
            , NUsize amount
            , ref int currSegmIndex
            , ref int currSegmOffset)
        {
            return segments.Forward(s => s.Length, amount, ref currSegmIndex, ref currSegmOffset);
        }

        public static NUsize Forward<T>
            ( in this ReadOnlyMemory<Memory<T>> segments
            , NUsize amount
            , ref int currSegmIndex
            , ref int currSegmOffset)
        {
            return segments.Forward(s => s.Length, amount, ref currSegmIndex, ref currSegmOffset);
        }

        #endregion

        #region Sub slice data

        public static ReadOnlyMemory<S> GetUnconsumedSlices<S>
            ( in this ReadOnlyMemory<S> segments
            , Func<S, int, S> getSlice
            , int currSegmIndex
            , int currSegmOffset)
        {
            if (segments.IsEmpty || currSegmIndex >= segments.Length)
                return segments;
            var sub = new S[segments.Length - currSegmIndex];
            for (var i = currSegmIndex; i < segments.Length; ++i)
            {
                if (i == currSegmIndex)
                    sub[i] = getSlice(segments.Span[i], currSegmOffset);
                else
                    sub[i] = segments.Span[i];
            }
            return sub;
        }

        public static ReadOnlyMemory<ReadOnlyMemory<T>> GetUnconsumedSlices<T>
            ( in this ReadOnlyMemory<ReadOnlyMemory<T>> segments
            , int currSegmIndex
            , int currSegmOffset)
        {
            return segments.GetUnconsumedSlices<ReadOnlyMemory<T>>
                ( (s, i) => s.Slice(start: i)
                , currSegmIndex
                , currSegmOffset);
        }

        public static ReadOnlyMemory<Memory<T>> GetUnconsumedSlices<T>
            ( in this ReadOnlyMemory<Memory<T>> segments
            , int currSegmIndex
            , int currSegmOffset)
        {
            return segments.GetUnconsumedSlices<Memory<T>>
                ( (s, i) => s.Slice(start: i)
                , currSegmIndex
                , currSegmOffset);
        }

        #endregion

        #region Debug print

        public static string DebugStr<S, T>
            ( in this ReadOnlyMemory<S> segments
            , Func<S, IEnumerable<T>> toIter
            , Func<T, string>? toStr = null)
        {
            if (toStr is null)
                toStr = (o) => o is null ? $"(null: {typeof(T).Name})" : o.ToString();
            var sb = new System.Text.StringBuilder();
            foreach (var s in segments.EnumerateSpan())
            {
                var e = string.Join(",", toIter(s).Select(toStr));
                sb.Append($"[{e}]");
            }
            return sb.ToString();
        }

        #endregion
    }

    public static class ObjGetHashcodeStrExtensions
    {
        public static string GetHashCodeStrX8(this object obj)
            => $"id:{obj.GetHashCode():X8}";
    }
}
