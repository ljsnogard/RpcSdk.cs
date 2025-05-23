namespace RpcPeerComSdk.Jun10
{
    using System;
    using System.Buffers.Binary;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using BufferKit;

    using Cysharp.Threading.Tasks;

    using LoggingSdk;

    using Newtonsoft.Json;

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
        private readonly SessionRx sessionRx_;

        private readonly Uri location_;

        private readonly AsyncMutex mutex_;

        private readonly string name_;

        private readonly IApiTypeBind apiTypeBind_;

        public PullAgent
            ( SessionRx sessionRx
            , Uri location
            , string name
            , IApiTypeBind apiTypeBind)
        {
            this.sessionRx_ = sessionRx;
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
            Memory<SessionMessage> buffer = new SessionMessage[1];
            Option<AsyncMutex.Guard> optGuard = Option.None;
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    throw new Exception();

                var recvRes = await this.sessionRx_.ReadAsync(buffer, token);
                if (recvRes.TryOk(out var mc, out var sessIoErr))
                    throw sessIoErr.AsException();

                if (mc != 1)
                    throw new Exception();

                var msg = buffer.Span[0];
                if (!msg.Size.TryInto(out ushort u16ContSize))
                    throw new Exception();

                Memory<byte> headerAndPayloadBuff = new byte[u16ContSize];
                var bc = 0;
                foreach (var segm in msg.Cont)
                {
                    var dst = headerAndPayloadBuff.Slice(bc, segm.Length);
                    segm.CopyTo(dst);
                    bc += segm.Length;
                }
                if (bc != u16ContSize)
                    throw new Exception($"unexpected cont len copied: expect({u16ContSize}), actual({bc})");

                Memory<byte> typeHexSpan = headerAndPayloadBuff.Slice(start: 0, length: 4);
                Memory<byte> msgSizeSpan = headerAndPayloadBuff.Slice(start: 4, length: 2);

                var typeHex = BinaryPrimitives.ReadUInt32BigEndian(typeHexSpan.Span);
                var msgSize = BinaryPrimitives.ReadUInt16BigEndian(msgSizeSpan.Span);

                ReadOnlyMemory<byte> msgPayload = headerAndPayloadBuff.Slice(start: 6, length: msgSize);

                return Result.Ok((typeHex, msgPayload));
            }
            catch (Exception e)
            {
                log.Debug($"{e}");
                throw;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
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
            ( SessionRx sessionRx
            , Uri location
            , string name
            , Func<Channel<TItem>> createBuffer
            , IApiTypeBind apiTypeBind
            ) 
            : this
                (new PullAgent(sessionRx, location, name, apiTypeBind)
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
                    var recvRes = await this.baseAgent_.SyncRecvAsync(token);
                    if (!recvRes.TryOk(out var buff, out var pullError))
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
