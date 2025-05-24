namespace RpcClientSdk.Mar07
{
    using System;
    using System.Buffers.Binary;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using Cysharp.Threading.Tasks;

    using LoggingSdk;

    using Newtonsoft.Json;

    using NsAnyLR;
    using NsBufferKit;

    using RpcPeerComSdk;

    static class PullConfig
    {
        private static readonly Lazy<JsonSerializerSettings> lazyJset_ = new(NewSettings_);

        private static JsonSerializerSettings NewSettings_()
            => new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.None };

        public static JsonSerializerSettings DemoDefaultSettings
            => lazyJset_.Value;

        public const int TYPE_HEX_SIZE = PushConfig.TYPE_HEX_SIZE;

        public const int JSON_BIN_SIZE = PushConfig.JSON_BIN_SIZE;
    }

    public readonly struct PullError : IPullError
    {
        public readonly IIoError InnerError;

        public PullError(IIoError innerError)
            => this.InnerError = innerError;

        public Exception AsException()
            => this.InnerError.AsException();
    }

    public sealed class PullAgent : IPullAgent
    {
        private readonly InputProxy<byte> input_;

        private readonly Uri location_;

        private readonly AsyncMutex mutex_;

        private readonly string name_;

        private readonly IApiTypeBind apiTypeBind_;

        public PullAgent
            ( InputProxy<byte> input
            , Uri location
            , string name
            , IApiTypeBind apiTypeBind)
        {
            this.input_ = input;
            this.location_ = location;
            this.mutex_ = new();
            this.name_ = name;
            this.apiTypeBind_ = apiTypeBind;
        }

        public Uri Location
            => this.location_;

        public string Name
            => this.name_;

        internal IApiTypeBind ApiTypeBind
            => this.apiTypeBind_;

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
                    throw readPrefixIoErr.AsException();
                if (readPrefixLen != (NUsize)prefixBuffLen)
                    throw new Exception($"Incomplete receive of type str Len, {prefixBuffLen} bytes needed but got {readPrefixLen}");

                var typeHexMem = prefixBuffer.Slice(offset: 0, length: PullConfig.TYPE_HEX_SIZE);
                var jsonLenMem = prefixBuffer.Slice(offset: PullConfig.TYPE_HEX_SIZE, length: PullConfig.JSON_BIN_SIZE);

                var typeHex = BinaryPrimitives.ReadUInt32BigEndian(typeHexMem.Span);
                var jsonLen = BinaryPrimitives.ReadUInt16BigEndian(jsonLenMem.Span);

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

                var jsonBuff = new Memory<byte>(new byte[jsonLen]);
                var readJsonRes = await this.input_.ReadAsync(jsonBuff, token);
                if (!readJsonRes.TryOk(out var readJsonLen, out var readJsonIoErr))
                    throw readJsonIoErr.AsException();
                if (readJsonLen != (NUsize)jsonLen)
                    throw new Exception($"Incomplete receive of msg str Len, {jsonLen} bytes needed but got {readJsonLen}");

#if DEBUG
                try
                {
                    var jsonHexBuilder = new StringBuilder();
                    for (var i = 0; i < jsonBuff.Length; ++i)
                        jsonHexBuilder.Append($"{jsonBuff.Span[i]:X2} ");
                    var jsonHex = jsonHexBuilder.ToString();

                    var jsonStr = System.Text.Encoding.UTF8.GetString(jsonBuff.Span);
                    log.Debug($"[{nameof(PullAgent)}.{nameof(SyncRecvAsync)}](Location: {this.Location}, Name: {this.Name}) typeHex: {typeHex:X8}, jsonLen: {jsonLen} ({jsonLen:X4}), json:\n{jsonStr}\n[\n{jsonHex}\n]");
                }
                finally
                { }
#endif
                return Result.Ok((typeHex, (ReadOnlyMemory<byte>)jsonBuff));
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

        private readonly SemaphoreSlim sema_;

        public PullAgent
            ( InputProxy<byte> input
            , Uri location
            , string name
            , Func<Channel<TItem>> createBuffer
            , IApiTypeBind apiTypeBind
            ) 
            : this
                (new PullAgent(input, location, name, apiTypeBind)
                , createBuffer)
        { }

        public PullAgent(
            PullAgent baseAgent,
            Func<Channel<TItem>> createBuffer)
        {
            this.baseAgent_ = baseAgent;
            this.items_ = createBuffer();
            this.cts_ = new CancellationTokenSource();
            this.rxloop_ = this.RxLoopAsync_();
            this.sema_ = new SemaphoreSlim(1, 1);
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
                    var apiTypebind = this.baseAgent_.ApiTypeBind;
                    var optType = apiTypebind.FindType(typeHex);
                    if (!optType.IsSome(out var dstType))
                    {
                        log.Warn($"[{nameof(PullAgent<TItem>)}.{nameof(RxLoopAsync_)}](items_: {this.items_.GetHashCode():X8}, Location: {this.baseAgent_.Location}) cannot find type with typeHex({typeHex:X8}).");
                        foreach (var kv in apiTypebind.GetTypeHexEntries())
                            log.Debug($"\t{kv.Item1:X8}: {kv.Item2.FullName}");
                        throw new Exception($"No types found with typeHex({typeHex:X8})");
                    }

                    var jsonStr = Encoding.UTF8.GetString(jsonHex.Span);
                    log.Debug($"[{nameof(PullAgent<TItem>)}.{nameof(RxLoopAsync_)}](items_: {this.items_.GetHashCode():X8}, Location: {this.baseAgent_.Location}) recv typeHex({typeHex:X8}), jsonStr({jsonStr})");

                    var obj = JsonConvert.DeserializeObject(jsonStr, dstType, PullConfig.DemoDefaultSettings);
                    if (obj is null)
                        throw new Exception($"Deserialized item is expected to be \"{typeof(TItem)}\" but got null");
                    if (obj is not TItem item)
                        throw new Exception($"Deserialized item is expected to be \"{typeof(TItem)}\" but got \"{obj.GetType().FullName}\"");

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
            try
            {
                await this.sema_.WaitAsync(token);
                var itemQueueRx = this.items_.Reader;
                var maybeItem = await itemQueueRx.ReadAsync(token);
                if (maybeItem is not TItem item)
                    throw new Exception($"[{nameof(PullAgent<TItem>)}.{nameof(DequeueAsync)}] empty item");
                return Result.Ok(item);
            }
            finally
            {
                if (this.sema_.CurrentCount == 0)
                    this.sema_.Release();
            }
        }

        async UniTask<Result<TItem, IPullError>> IPullAgent<TItem>.DequeueAsync(CancellationToken token)
        {
            var x = await this.DequeueAsync(token);
            return x.MapErr(e => (IPullError)e);
        }
    }

}
