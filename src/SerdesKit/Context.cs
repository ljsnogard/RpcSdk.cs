namespace SerdesKit
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using BufferKit;
    using System.Linq;
    using LoggingSdk;
    using VisitAsyncUtils;

    public interface ISerdesTypeContext
    {
        public Type DataType { get; }

        public bool HasSubTypes { get; }

        public bool HasFields { get; }

        public uint TypeHex { get; }

        public UniTask<ReadOnlyMemory<ISerdesTypeContext>> GetSubTypesAsync(CancellationToken token = default);

        public UniTask<ReadOnlyMemory<(string, ISerdesTypeContext)>> GetFieldsAsync(CancellationToken token = default);
    }

    public static class StrHashExtensions
    {
        public static uint GetStableHashCode(this string str)
        {
            unchecked
            {
                uint hash1 = 5381;
                uint hash2 = hash1;

                for (int i = 0; i < str.Length && str[i] != '\0'; i += 2)
                {
                    hash1 = ((hash1 << 5) + hash1) ^ str[i];
                    if (i == str.Length - 1 || str[i + 1] == '\0')
                        break;
                    hash2 = ((hash2 << 5) + hash2) ^ str[i + 1];
                }
                return hash1 + (hash2 * 1566083941);
            }
        }

        public static uint GetTypeHex(this Type type)
            => type.FullName.GetStableHashCode();

        public static uint GetTypeHexWithRound(this Type type, uint round)
        {
            var name = type.FullName;
            var hex = type.GetTypeHex();
            while (round != 0)
            {
                name = name + $"_{hex:X8}";
                hex = name.GetStableHashCode();
                round -= 1u;
            }
            return hex;
        }
    }

    public sealed class ConcreteSerdesTypeContext : ISerdesTypeContext
    {
        private readonly Type dataType_;

            /// <summary>
        /// The type hashcode based on type name and fields
        /// </summary>
        private readonly uint typeHex_;

        /// <summary>
        /// Types that applies the Liskov Substitution principle and can place into declation of this type.
        /// </summary>
        private readonly ReadOnlyMemory<ConcreteSerdesTypeContext> subTypes_;

        /// <summary>
        /// Fields' names and their types orders in declaration order.
        /// </summary>
        private readonly ReadOnlyMemory<(string, ConcreteSerdesTypeContext)> fields_;

        public static UniTask<Option<ConcreteSerdesTypeContext>> FindContextForTypeAsync<T>(CancellationToken token = default)
            => ConcreteSerdesTypeContext.GetGlobalCtxCache().FindWithTypeAsync(typeof(T), token);

        public static UniTask<Option<FnAcceptVisitorAsync<T, F, V>>> FindAcceptFnAsync<T, F, V>(CancellationToken token = default)
            where F: IVisitorFactory<T, V>
            where V: IVisitor<T>
        {
            throw new NotImplementedException();
        }

        public ConcreteSerdesTypeContext(
            Type dataType,
            uint typeHex,
            ReadOnlyMemory<ConcreteSerdesTypeContext> subTypes,
            ReadOnlyMemory<(string, ConcreteSerdesTypeContext)> fields)
        {
            this.dataType_ = dataType;
            this.subTypes_ = subTypes;
            this.fields_ = fields;
            this.typeHex_ = typeHex;
        }

        public Type DataType
            => this.dataType_;

        public bool HasSubTypes
            => this.subTypes_.Length > 0;

        public bool HasFields
            => this.fields_.Length > 0;

        public ReadOnlyMemory<ConcreteSerdesTypeContext> SubTypes
            => this.subTypes_;

        public ReadOnlyMemory<(string, ConcreteSerdesTypeContext)> Fields
            => this.fields_;

        public uint TypeHex
            => this.typeHex_;

        UniTask<ReadOnlyMemory<ISerdesTypeContext>> ISerdesTypeContext.GetSubTypesAsync(CancellationToken token)
        {
            var list = new ReadOnlyMemory<ISerdesTypeContext>(this
                .subTypes_
                .EnumerateSpan()
                .Select(c => c as ISerdesTypeContext)
                .ToArray()
            );
            return new(list);
        }

        UniTask<ReadOnlyMemory<(string, ISerdesTypeContext)>> ISerdesTypeContext.GetFieldsAsync(CancellationToken token)
        {
            var list = new ReadOnlyMemory<(string, ISerdesTypeContext)>(this
                .fields_
                .EnumerateSpan()
                .Select(t => (t.Item1, t.Item2 as ISerdesTypeContext))
                .ToArray()
            );
            return new(list);
        }

        public static IEnumerable<Type> PrimitiveTypes
            => primitiveTypes_;

        private static readonly Type[] primitiveTypes_ = new[] {
            typeof(bool), typeof(char), typeof(nint), typeof(nuint),
            typeof(byte), typeof(sbyte), typeof(ushort), typeof(short), typeof(uint), typeof(int), typeof(ulong), typeof(long),
            typeof(float), typeof(double), typeof(decimal),
            typeof(string),
        };

        internal sealed class GlobalCtxCache
        {
            private readonly AsyncMutex mutex_;

            private readonly Dictionary<uint, ConcreteSerdesTypeContext> hexCtxDict_;

            public GlobalCtxCache(IEnumerable<Type> initTypes)
            {
                this.mutex_ = new();
                this.hexCtxDict_ = new();

                foreach (var pt in initTypes)
                {
                    var hexResult = this.FindNonConflictTypeHex_(pt);
                    if (!hexResult.IsOk(out var hex))
                    {
                        var m = $"Failed in adding initType: {pt}";
                        throw new Exception(m);
                    }
                    var ctx = new ConcreteSerdesTypeContext(
                        pt,
                        hex,
                        ReadOnlyMemory<ConcreteSerdesTypeContext>.Empty,
                        ReadOnlyMemory<(string, ConcreteSerdesTypeContext)>.Empty
                    );
                    this.hexCtxDict_[hex] = ctx;
                }
            }

            public async UniTask<Option<ConcreteSerdesTypeContext>> FindWithHexAsync(uint typeHex, CancellationToken token = default)
            {
                var log = Logger.Shared;
                Option<AsyncMutex.Guard> optGuard = Option.None;
                try
                {
                    optGuard = await this.mutex_.AcquireAsync(token);
                    if (!optGuard.IsSome(out var guard))
                        return Option.None;

                    if (this.hexCtxDict_.TryGetValue(typeHex, out var ctx))
                        return Option.Some(ctx);
                    else
                        return Option.None;
                }
                catch (Exception ex)
                {
                    log.Error($"[{nameof(GlobalCtxCache)}.{nameof(FindWithHexAsync)}] {ex}");
                    throw;
                }
                finally
                {
                    if (optGuard.IsSome(out var guard))
                        guard.Dispose();
                }
            }

            public async UniTask<Option<ConcreteSerdesTypeContext>> FindWithTypeAsync(Type dataType, CancellationToken token = default)
            {
                var log = Logger.Shared;
                Option<AsyncMutex.Guard> optGuard = Option.None;
                try
                {
                    optGuard = await this.mutex_.AcquireAsync(token);
                    if (!optGuard.IsSome(out var guard))
                        return Option.None;

                    var round = 0u;
                    uint typeHex = 0u;
                    while (true)
                    {
                        typeHex = dataType.GetTypeHexWithRound(round);
                        if (!this.hexCtxDict_.TryGetValue(typeHex, out var ctx))
                            break;
                        if (ctx.DataType == dataType)
                            return Option.Some(ctx);
                        round++;
                        log.Debug($"[{nameof(GlobalCtxCache)}.{nameof(FindWithTypeAsync)}] round: {round}, type: {dataType}");
                        continue;
                    }
                    return Option.None;
                }
                catch (Exception ex)
                {
                    log.Error($"[{nameof(GlobalCtxCache)}.{nameof(FindWithTypeAsync)}] {ex}");
                    throw;
                }
                finally
                {
                    if (optGuard.IsSome(out var guard))
                        guard.Dispose();
                }
            }

            public async UniTask<Result<ConcreteSerdesTypeContext, ConcreteSerdesTypeContext>> FindOrAddAsync(
                Type dataType,
                Func<uint, Type, CancellationToken, UniTask<ConcreteSerdesTypeContext>> createContext,
                CancellationToken token = default)
            {
                var log = Logger.Shared;
                Option<AsyncMutex.Guard> optGuard = Option.None;
                try
                {
                    optGuard = await this.mutex_.AcquireAsync(token);
                    if (!optGuard.IsSome(out var guard))
                    {
                        var m = $"[{nameof(GlobalCtxCache)}.{nameof(FindOrAddAsync)}] operation cancelled during mutex acq";
                        throw new Exception(m);
                    }
                    var findTypeHexResult = this.FindNonConflictTypeHex_(dataType);
                    if (!findTypeHexResult.TryOk(out var typeHex, out var existingCtx))
                        return Result.Err(existingCtx);

                    var createdCtx = await createContext(typeHex, dataType, token);
                    this.hexCtxDict_.Add(typeHex, createdCtx);

                    log.Info($"[{nameof(GlobalCtxCache)}.{nameof(FindOrAddAsync)}] Added context, hex: {typeHex}, type: {dataType.FullName}.");
                    return Result.Ok(createdCtx);
                }
                catch (OperationCanceledException)
                {
                    log.Error($"[{nameof(GlobalCtxCache)}.{nameof(FindOrAddAsync)}] operation cancelled during createContext");
                    throw;
                }
                catch (Exception ex)
                {
                    log.Error($"[{nameof(GlobalCtxCache)}.{nameof(FindOrAddAsync)}] {ex}");
                    throw;
                }
                finally
                {
                    if (optGuard.IsSome(out var guard))
                        guard.Dispose();
                }
            }

            private Result<uint, ConcreteSerdesTypeContext> FindNonConflictTypeHex_(Type dataType)
            {
                var log = Logger.Shared;
                uint round = 0u;
                uint typeHex = 0u;
                while (true)
                {
                    typeHex = dataType.GetTypeHexWithRound(round);
                    if (this.hexCtxDict_.TryGetValue(typeHex, out var ctx))
                    {
                        if (ctx.DataType == dataType)
                            return Result.Err(ctx);
                        round++;
                        log.Debug($"[{nameof(GlobalCtxCache)}.{nameof(FindNonConflictTypeHex_)}] round: {round}, type: {dataType}");
                        continue;
                    }
                    break;
                }
                return Result.Ok(typeHex);
            }
        }

        private static Lazy<GlobalCtxCache> lazyCache_ = new Lazy<GlobalCtxCache>(() => new GlobalCtxCache(PrimitiveTypes));

        internal static GlobalCtxCache GetGlobalCtxCache()
            => ConcreteSerdesTypeContext.lazyCache_.Value;
    }
}