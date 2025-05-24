namespace SerdesKit.MessagePack
{
    using System;
    using System.Threading;

    using Cysharp.Threading.Tasks;    

    using LoggingSdk;

    using NsAnyLR;
    using NsBufferKit;

    using VisitAsyncUtils;

    public sealed class Serializer<T> :
        ISerializer<T>,
        IVisitorFactory<T, Visitor<T>>
    {
        private readonly BufferedWriter writer_;

        private readonly ConcreteSerdesTypeContext typeCtx_;

        private readonly FnAcceptVisitorAsync<T, Serializer<T>, Visitor<T>> acceptFn_;

        private readonly ISerdesTypeContext? superCtx_;

        public Serializer(
            BufferedWriter writer,
            ConcreteSerdesTypeContext typeCtx,
            FnAcceptVisitorAsync<T, Serializer<T>, Visitor<T>> acceptFn,
            ISerdesTypeContext? superCtx = null)
        {
            this.writer_ = writer;
            this.typeCtx_ = typeCtx;
            this.acceptFn_ = acceptFn;
            this.superCtx_ = superCtx;
        }

        public async UniTask<Result<NUsize, ISerdesError>> SerializeAsync(T data, CancellationToken token = default)
        {
            var log = Logger.Shared;
            try
            {
                // 序列化各个字段, 根据生成的代码, 这里会调用 GetVisitorAsync
                var succ = await this.acceptFn_(data, this, string.Empty, token);
                if (succ)
                    return Result.Ok(this.WrittenSize);
                else
                    throw new Exception();
            }
            catch (Exception ex)
            {
                log.Error($"[{nameof(Serializer<T>)}.{nameof(SerializeAsync)}] {ex}");
                throw;
            }
        }

        internal BufferedWriter Writer
            => this.writer_;

        internal ConcreteSerdesTypeContext TypeCtx
            => this.typeCtx_;

        internal ISerdesTypeContext? SuperCtx { get; set; }

        internal NUsize WrittenSize { get; set; }

        UniTask<Visitor<T>> IVisitorFactory<T, Visitor<T>>.GetVisitorAsync(T host, CancellationToken token)
            => throw new NotImplementedException();
    }

    public readonly struct Visitor<T> : IVisitor<T>, IDisposable
    {
        private readonly Serializer<T> serializer_;

        internal Visitor(Serializer<T> serializer)
            => this.serializer_ = serializer;

        public async UniTask<bool> VisitAsync<X>(X field, string key = "", CancellationToken token = default)
        {
            var tryWriteResult = await this.serializer_.Writer.TryWriteAsync(field, token);
            if (tryWriteResult.TryOk(out var c, out var serializer))
            {
                this.serializer_.WrittenSize += c;
                return true;
            }
            serializer.SuperCtx = this.serializer_.TypeCtx;
            var serializeRes = await serializer.SerializeAsync(field, token);
            if (serializeRes.TryOk(out c, out var err))
            {
                this.serializer_.WrittenSize += c;
                return true;
            }
            var innerExc = err.AsException();
            var message = $"[{nameof(Visitor<T>)}.{nameof(VisitAsync)}'{typeof(X).Name}] serializing for type({typeof(T)}, in field({key}), fieldType({typeof(X)}), ex: {innerExc.Message}";
            throw new Exception(message, innerExc);
        }

        public void Dispose()
        {

        }
    }
}