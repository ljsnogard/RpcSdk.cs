namespace SerdesKit.Json
{
    using System;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using VisitAsyncUtils;

    using BufferKit;
    using LoggingSdk;

    internal abstract class Serializer
    {
        public static readonly string K_TYPEHEX_KEY = "_$typeHex_";
    }

    public sealed class Serializer<T> : ISerializer<T>, IVisitorFactory<T, Visitor<T>>, IRebindableVisitorFactory
    {
        private readonly TxProxy<byte> tx_;

        private readonly ConcreteSerdesTypeContext typeCtx_;

        private readonly FnAcceptVisitorAsync<T, Serializer<T>, Visitor<T>> acceptFn_;

        private readonly ISerdesTypeContext? superCtx_;

        private readonly AsyncMutex mutex_;

        public Serializer(
            TxProxy<byte> tx,
            ConcreteSerdesTypeContext typeCtx,
            FnAcceptVisitorAsync<T, Serializer<T>, Visitor<T>> acceptFn,
            ISerdesTypeContext? superCtx = null)
        {
            this.tx_ = tx;
            this.typeCtx_ = typeCtx;
            this.acceptFn_ = acceptFn;
            this.superCtx_ = superCtx;
            this.mutex_ = new();
        }

        public async UniTask<Result<NUsize, ISerdesError>> SerializeAsync(T data, CancellationToken token = default)
        {
            var log = Logger.Shared;
            Option<AsyncMutex.Guard> optGuard = Option.None;
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Result.Ok(NUsize.Zero);

                var succ = false;

                if (this.typeCtx_.HasSubTypes || this.typeCtx_.DataType.IsAbstract || this.typeCtx_.DataType.IsInterface)
                {

                }

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
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        public async UniTask<Serializer<THost>> GetFactoryAsync<THost, TVisitor>(CancellationToken token)
            where TVisitor : IVisitor<THost>
        {
            var optCtx = await ConcreteSerdesTypeContext.FindContextForTypeAsync<THost>(token);
            if (!optCtx.IsSome(out var ctx))
                throw new Exception();

            var optFn = await ConcreteSerdesTypeContext.FindAcceptFnAsync<THost, Serializer<THost>, Visitor<THost>>(token);
            if (!optFn.IsSome(out var acceptFn))
                throw new Exception();

            return new Serializer<THost>(this.tx_, ctx, acceptFn, this.TypeCtx);
        }

        UniTask<Visitor<T>> IVisitorFactory<T, Visitor<T>>.GetVisitorAsync(T host, CancellationToken token)
            => UniTask.FromResult(new Visitor<T>(this));

        async UniTask<IVisitorFactory<THost, TVisitor>> IRebindableVisitorFactory.GetFactoryAsync<THost, TVisitor>(CancellationToken token)
        {
            var log = Logger.Shared;
            try
            {
                var s = await this.GetFactoryAsync<THost, TVisitor>(token);
                if (s is IVisitorFactory<THost, TVisitor> r)
                    return r;
                else
                    throw new Exception();
            }
            catch (Exception e)
            {
                log.Error($"[{nameof(Serializer<T>)}.{nameof(GetFactoryAsync)}`{typeof(THost).Name}_{typeof(TVisitor).Name}] {e}");
                throw;
            }
        }

        internal TxProxy<byte> Tx
            => this.tx_;

        internal ConcreteSerdesTypeContext TypeCtx
            => this.typeCtx_;

        internal ISerdesTypeContext? SuperCtx { get; set; }

        internal NUsize WrittenSize { get; set; }
    }

    public readonly struct Visitor<T> : IVisitor<T>, IDisposable
    {
        private readonly Serializer<T> serializer_;

        internal Visitor(Serializer<T> serializer)
            => this.serializer_ = serializer;

        public async UniTask<bool> VisitAsync<X>(X field, string key, CancellationToken token = default)
        {
            var w = new DataWriter(this.serializer_.Tx);
            var opt = await w.WritePropertyNameAndSep(key);
            if (!opt.TryOk(out _, out var ioErr))
                throw ioErr.AsException();

            if (TryWrite(w, field))
                return true;

            var optCtx = await ConcreteSerdesTypeContext.FindContextForTypeAsync<X>(token);
            if (!optCtx.IsSome(out var ctx))
                throw new Exception();

            var optFn = await ConcreteSerdesTypeContext.FindAcceptFnAsync<X, Serializer<X>, Visitor<X>>(token);
            if (!optFn.IsSome(out var acceptFn))
                throw new Exception();

            var serializer = new Serializer<X>(this.serializer_.Tx, ctx, acceptFn, this.serializer_.TypeCtx);
            var serializeRes = await serializer.SerializeAsync(field, token);
            if (serializeRes.TryOk(out var c, out var serdesErr))
            {
                this.serializer_.WrittenSize += c;
                return true;
            }
            var innerExc = serdesErr.AsException();
            var message = $"[{nameof(Visitor<T>)}.{nameof(VisitAsync)}'{typeof(X).Name}] serializing for type({typeof(T)}, in field({key}), fieldType({typeof(X)}), ex: {innerExc.Message}";
            throw new Exception(message, innerExc);
        }

        public void Dispose()
        {

        }

        /// <summary>
        /// Try direct write primitive types
        /// </summary>
        /// <returns>Returns false if V is not primitive types.</returns>
        private static bool TryWrite<V>(DataWriter w, V v)
        {
            // if (v is null)
            // {
            //     w.WriteNullValue();
            //     return true;
            // }
            // switch (v)
            // {
            //     case byte x: w.WriteNumberValue(x); return true;
            //     case sbyte x: w.WriteNumberValue(x); return true;

            //     case ushort x: w.WriteNumberValue(x); return true;
            //     case short x: w.WriteNumberValue(x); return true;

            //     case uint x: w.WriteNumberValue(x); return true;
            //     case int x: w.WriteNumberValue(x); return true;

            //     case ulong x: w.WriteNumberValue(x); return true;
            //     case long x: w.WriteNumberValue(x); return true;

            //     case float x: w.WriteNumberValue(x); return true;
            //     case double x: w.WriteNumberValue(x); return true;
            //     case decimal x: w.WriteNumberValue(x); return true;

            //     case string x: w.WriteStringValue(x); return true;
            //     case bool x: w.WriteBooleanValue(x); return true;
            //     case char x: w.WriteStringValue(x.ToString()); return true;

            //     default: return false;
            // }
            throw new NotImplementedException();
        }
    }
}