namespace RpcPeerComSdk.Mar07
{
    using System;
    using System.Buffers.Binary;
    using System.Collections.Generic;

    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using BufferKit;

    using Cysharp.Threading.Tasks;

    using LoggingSdk;

    using SerdesKit;

    static class PullConfig
    {
        public const int TYPE_HEX_SIZE = PushConfig.TYPE_HEX_SIZE;

        public const int JSON_BIN_SIZE = PushConfig.JSON_BIN_SIZE;
    }

    public readonly struct PullError : IPullError
    {
        private readonly Exception exception_;

        public PullError(Exception exception)
            => this.exception_ = exception;

        public Exception AsException()
            => this.exception_;
    }

    public sealed class PullAgent : IPullAgent
    {
        private readonly InputProxy<byte> input_;

        private readonly Uri location_;

        private readonly AsyncMutex mutex_;

        private readonly string name_;

        public PullAgent(InputProxy<byte> input, Uri location, string name)
        {
            this.input_ = input;
            this.location_ = location;
            this.mutex_ = new();
            this.name_ = name;
        }

        public Uri Location
            => this.location_;

        public string Name
            => this.name_;

        /// <summary>
        /// 接收来自远端推送的数据，这些数据可完成反序列化为指定的对象，对象的类型由 TypeHex 指示
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception> <summary>
        /// 
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        internal async UniTask<Result<(uint, ReadOnlyMemory<byte>), PullError>> SyncRecvAsync(CancellationToken token = default)
        {
            var log = Logger.Shared;
            var acqRes = await this.mutex_.AcquireAsync(token);
            if (!acqRes.IsSome(out var guard))
                throw new Exception();
            try
            {
                var prefixBuffLen = PullConfig.TYPE_HEX_SIZE + PullConfig.JSON_BIN_SIZE;
                var prefixBuffer = new Memory<byte>(new byte[prefixBuffLen]);

                // log.Debug($"[{nameof(PullAgent)}.{nameof(SyncRecvAsync)}](Location: {this.Location}, Name: {this.Name}) before FillAsync`Memory ({prefixBuffLen} bytes)");

                var readPrefixRes = await this.input_.ReadAsync(prefixBuffer, token);
                if (!readPrefixRes.TryOk(out var readPrefixLen, out var readPrefixIoErr))
                    return Result.Err(new PullError());
                if (readPrefixLen != (NUsize)prefixBuffLen)
                    throw new Exception($"Incomplete receive of type str Len, {prefixBuffLen} bytes needed but got {readPrefixLen}");

                var typeHexMem = prefixBuffer.Slice(offset: 0, length: PullConfig.TYPE_HEX_SIZE);
                var msgSizeMem = prefixBuffer.Slice(offset: PullConfig.TYPE_HEX_SIZE, length: PullConfig.JSON_BIN_SIZE);

                var typeHex = BinaryPrimitives.ReadUInt32BigEndian(typeHexMem.Span);
                var msgSize = BinaryPrimitives.ReadUInt16BigEndian(msgSizeMem.Span);

#if DEBUG
                // try
                // {
                //     var prefixHexBuilder = new StringBuilder();
                //     for(var i = 0; i < prefixBuffer.Length; ++i)
                //         prefixHexBuilder.Append($"{prefixBuffer.Span[i]:X2} ");
                //     var prefixHex = prefixHexBuilder.ToString();
                //     log.Debug($"[{nameof(PullAgent)}.{nameof(SyncRecvAsync)}](Location: {this.Location}, Name: {this.Name}) typeHex: {typeHex:X8}, jsonLen: {jsonLen} ({jsonLen:X4}), prefixHex: [\n{prefixHex}\n]");
                // }
                // finally
                // { }
#endif

                var msgBuff = new Memory<byte>(new byte[msgSize]);
                var readMsgRes = await this.input_.ReadAsync(msgBuff, token);
                if (!readMsgRes.TryOk(out var readMsgLen, out var readMsgIoErr))
                    return Result.Err(new PullError());
                if (readMsgLen != (NUsize)msgSize)
                    throw new Exception($"Incomplete receive of msg str Len, {msgSize} bytes needed but got {readMsgLen}");

#if DEBUG
                // try
                // {
                //     var jsonHexBuilder = new StringBuilder();
                //     for (var i = 0; i < jsonBuff.Length; ++i)
                //         jsonHexBuilder.Append($"{jsonBuff.Span[i]:X2} ");
                //     var jsonHex = jsonHexBuilder.ToString();

                //     var jsonStr = System.Text.Encoding.UTF8.GetString(jsonBuff.Span);
                //     log.Debug($"[{nameof(PullAgent)}.{nameof(SyncRecvAsync)}](Location: {this.Location}, Name: {this.Name}) typeHex: {typeHex:X8}, jsonLen: {jsonLen} ({jsonLen:X4}), json:\n{jsonStr}\n[\n{jsonHex}\n]");
                // }
                // finally
                // { }
#endif
                return Result.Ok((typeHex, (ReadOnlyMemory<byte>)msgBuff));
            }
            catch (Exception e)
            {
                log.Debug($"{e}");
                throw;
            }
            finally
            {
                guard.Dispose();
            }
        }

        internal InputProxy<byte> SourceInput
            => this.input_;

        internal IDeserializer<TItem> GetDeserializer<TItem>(IUnbufferedInput<byte> input)
            => throw new NotImplementedException();

        public UniTask<Result<RxProxy<byte>, PullError>> RecvAsync(CancellationToken token = default)
            => throw new NotImplementedException();

        async UniTask<Result<RxProxy<byte>, IPullError>> IPullAgent.RecvAsync(CancellationToken token)
        {
            var x = await this.RecvAsync(token);
            return x.MapErr(e => (IPullError)e);
        }
    }

    public sealed class PullAgent<TItem> : IPullAgent<TItem>
    {
        private readonly PullAgent baseAgent_;

        private readonly Cysharp.Threading.Tasks.Channel<TItem> items_;

        private readonly CancellationTokenSource cts_;

        private readonly Task rxloop_;

        private readonly AsyncMutex mutex_;

        public PullAgent(
            InputProxy<byte> input,
            Uri location,
            string name,
            Func<Channel<TItem>> createBuffer) : this(new PullAgent(input, location, name), createBuffer)
        { }

        public PullAgent(
            PullAgent baseAgent,
            Func<Channel<TItem>> createBuffer)
        {
            this.baseAgent_ = baseAgent;
            this.items_ = createBuffer();
            this.cts_ = new CancellationTokenSource();
            this.rxloop_ = this.RxLoopAsync_();
            this.mutex_ = new();
        }

        public Uri Location
            => this.baseAgent_.Location;

        public async UniTask StopAsync()
        {
            this.cts_.Cancel();
            await this.rxloop_;
        }

        private async Task RxLoopAsync_()
        {
            var log = Logger.Shared;
            var token = this.cts_.Token;
            try
            {
                log.Debug($"[{nameof(PullAgent<TItem>)}.{nameof(RxLoopAsync_)}](items_: {this.items_.GetHashCode():X8}, Location: {this.baseAgent_.Location}) starts loop.");
                var itemQueueTx = this.items_.Writer;

                while (true)
                {
                    if (token.IsCancellationRequested)
                    {
                        log.Debug($"[{nameof(PullAgent<TItem>)}.{nameof(RxLoopAsync_)}](items_: {this.items_.GetHashCode():X8}, Location: {this.baseAgent_.Location}) exits loop.");
                        break;
                    }
                    var maybeBuff = await this.baseAgent_.SyncRecvAsync(token);
                    if (!maybeBuff.TryOk(out var buff, out var pullError))
                        throw pullError.AsException();

                    var (typeHex, jsonHex) = buff;
                    var findTypeResult = await PullAgent<TItem>.FindTypeWithHexAsync(typeHex);
                    if (!findTypeResult.TryOk(out var dstType, out var cachedTypes))
                    {
                        log.Warn($"[{nameof(PullAgent<TItem>)}.{nameof(RxLoopAsync_)}](items_: {this.items_.GetHashCode():X8}, Location: {this.baseAgent_.Location}) cannot find type with typeHex({typeHex:X8}).");
                        foreach (var kv in cachedTypes)
                            log.Debug($"\t{kv.Key:X8}: {kv.Value.FullName}");
                        throw new Exception($"No types found with typeHex({typeHex:X8})");
                    }

                    var des = this.baseAgent_.GetDeserializer<TItem>(this.baseAgent_.SourceInput);
                    var deserializeRes = await des.DeserializeAsync(token);
                    if (!deserializeRes.TryOk(out var item, out var desErr))
                        throw desErr.AsException();

                    var writeSucc = itemQueueTx.TryWrite(item);
                    if (!writeSucc)
                        throw new Exception($"[{nameof(PullAgent<TItem>)}.{nameof(RxLoopAsync_)}] write item failed.");
                }
            }
            catch (OperationCanceledException)
            {
                log.Debug($"[{nameof(PullAgent<TItem>)}.{nameof(RxLoopAsync_)}](items_: {this.items_.GetHashCode():X8}, Location: {this.baseAgent_.Location}) exits loop.");
                return;
            }
            catch (Exception e)
            {
                log.Error($"[{nameof(PullAgent<TItem>)}.{nameof(RxLoopAsync_)}](items_: {this.items_.GetHashCode():X8}, Location: {this.baseAgent_.Location}) Exception: {e}");
                throw;
            }
        }

        async UniTask<Result<RxProxy<byte>, IPullError>> IPullAgent.RecvAsync(CancellationToken token)
        {
            var x = await this.baseAgent_.RecvAsync(token);
            return x.MapErr(e => (IPullError)e);
        }

        public async UniTask<Result<TItem, PullError>> DequeueAsync(CancellationToken token = default)
        {
            Option<AsyncMutex.Guard> optGuard = Option.None;
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);
                var itemQueueRx = this.items_.Reader;
                var maybeItem = await itemQueueRx.ReadAsync(token);
                if (maybeItem is not TItem item)
                    throw new Exception($"[{nameof(PullAgent<TItem>)}.{nameof(DequeueAsync)}] empty item");
                return Result.Ok(item);
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        async UniTask<Result<TItem, IPullError>> IPullAgent<TItem>.DequeueAsync(CancellationToken token)
        {
            var x = await this.DequeueAsync(token);
            return x.MapErr(e => (IPullError)e);
        }

        private static Lazy<AsyncMutex> lazyTypeDictMutex_ = new(() => new AsyncMutex());

        private static readonly Dictionary<uint, Type> typeDict_ = new();

        private static async Task<Result<Type, Dictionary<uint, Type>>> FindTypeWithHexAsync(uint hexCode)
        {
            const string ASM_PREFIX = "LiquidRainbow";
            var log = Logger.Shared;
            var typeDictMutex = lazyTypeDictMutex_.Value;
            Option<AsyncMutex.Guard> optGuard = Option.None;
            try
            {
                optGuard = await typeDictMutex.AcquireAsync();
                while (true)
                {
                    if (PullAgent<TItem>.typeDict_.TryGetValue(hexCode, out var dstType))
                        return Result.Ok(dstType);

                    if (PullAgent<TItem>.typeDict_.Count > 0)
                        break;

                    var assemblies =
                        from a in AppDomain.CurrentDomain.GetAssemblies()
                        where a.FullName.StartsWith(ASM_PREFIX)
                        select a;
                    var types =
                        from asm in assemblies
                        from t in asm.GetTypes()
                        where typeof(TItem).IsAssignableFrom(t)
                        select t;
                    foreach (Type t in types)
                    {
                        var key = t.FullName.GetStableHashCode();
                        if (PullAgent<TItem>.typeDict_.TryAdd(key, t))
                            log.Info($"Added type({t.FullName}) with key: {key:X8}");
                        else
                            log.Error($"Failed adding type({t.FullName}) with key: {key:X8}");
                    }
                }
                return Result.Err(PullAgent<TItem>.typeDict_);
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }
    }
}
