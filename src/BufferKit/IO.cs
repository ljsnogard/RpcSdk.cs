namespace NsBufferKit
{
    using System;
    using System.Threading;

    using NsAnyLR;

    using Cysharp.Threading.Tasks;

    public interface IIoError
    {
        public Exception AsException();
    }

    /// <summary>
    /// 无缓冲输入设备，能将数据从设备输入到指定的缓冲区
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IUnbufferedInput<T>
    {
        public UniTask<Result<NUsize, IIoError>> ReadAsync(
            Memory<T> target,
            CancellationToken token = default
        );
    }

    /// <summary>
    /// 无缓冲输出设备，能将数据从缓冲区输出
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IUnbufferedOutput<T>
    {
        public UniTask<Result<NUsize, IIoError>> WriteAsync(
            ReadOnlyMemory<T> source,
            CancellationToken token = default
        );
    }
}