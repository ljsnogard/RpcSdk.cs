namespace SerdesKit
{
    using System;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using NsAnyLR;
    using NsBufferKit;

    public interface ISerdesError
    {
        public Exception AsException();
    }

    /// <summary>
    /// 将类型 T 对象序列化到 Serializer 内置的缓存中
    /// </summary>
    /// <typeparam name="T">要序列化的对象的类型</typeparam>
    public interface ISerializer<T>
    {
        public UniTask<Result<NUsize, ISerdesError>> SerializeAsync(T data, CancellationToken token = default);
    }

    /// <summary>
    /// 从 Deserializer 内置的缓存中反序列化出类型 T 对象
    /// </summary>
    /// <typeparam name="T">反序列化目标的类型</typeparam>
    public interface IDeserializer<T>
    {
        public UniTask<Result<T, ISerdesError>> DeserializeAsync(CancellationToken token = default);
    }
}
