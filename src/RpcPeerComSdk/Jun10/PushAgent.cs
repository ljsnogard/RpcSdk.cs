namespace RpcPeerComSdk.Jun10
{
    using System;
    using System.Buffers.Binary;
    using System.Text;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using LoggingSdk;

    using Newtonsoft.Json;

    using NsAnyLR;
    using NsBufferKit;

    using RpcPeerComSdk;

    using static NsBufferKit.NsUtils.StrHashExtensions;

    static class PushConfig
    {
        private static readonly Lazy<JsonSerializerSettings> lazyJset_ = new(NewSettings_);

        private static JsonSerializerSettings NewSettings_()
            => new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.None };

        public static JsonSerializerSettings DemoDefaultSettings
            => lazyJset_.Value;

        public const int TYPE_HEX_SIZE = sizeof(uint);

        public const int JSON_BIN_SIZE = sizeof(ushort);
    }

    public readonly struct PushError : IPushError
    {
        public readonly IIoError InnerError;

        public PushError(IIoError innerError)
            => this.InnerError = innerError;

        public Exception AsException()
            => this.InnerError.AsException();
    }

    public sealed class PushAgent : IPushAgent
    {
        private readonly SessionTx sessionTx_;

        private readonly Uri location_;

        private readonly AsyncMutex mutex_;

        private readonly string name_;

        public PushAgent(SessionTx sessionTx, Uri location, string name)
        {
            this.sessionTx_ = sessionTx;
            this.location_ = location;
            this.mutex_ = new();
            this.name_ = name;
        }

        public Uri Location
            => this.location_;

        public string Name
            => this.name_;

        internal AsyncMutex Mutex
            => this.mutex_;

        internal async UniTask<Result<NUsize, PushError>> SyncSendAsync
            ( uint typeHex
            , ReadOnlyMemory<byte> msgPayload
            , CancellationToken token = default)
        {
            var log = Logger.Shared;

            if (!msgPayload.NUsizeLength().TryInto(out ushort u16msgSize))
                throw new Exception($"message size({msgPayload.Length}) invalid");

            Option<AsyncMutex.Guard> optGuard = Option.None();
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    throw new Exception();

                var typeHexValBuffer = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(typeHexValBuffer, typeHex);
                var msgSizeValBuffer = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(msgSizeValBuffer, u16msgSize);

                // ReadOnlyMemory<byte> typeHexKey = Encoding.UTF8.GetBytes(StdHeader.K_TYPE_HEX_HEADER_KEY);
                ReadOnlyMemory<byte> typeHexVal = typeHexValBuffer;
                // ReadOnlyMemory<byte> msgSizeKey = Encoding.UTF8.GetBytes(StdHeader.K_MSG_SIZE_HEADER_KEY);
                ReadOnlyMemory<byte> msgSizeVal = msgSizeValBuffer;

                var message = new SessionMessage(new[] { typeHexVal, msgSizeVal, msgPayload });
                ReadOnlyMemory<SessionMessage> msg = new[] { message };
                var x = await this.sessionTx_.WriteAsync(msg, token);
                if (x.TryOk(out var len, out var ioErr))
                    log.Debug($"[{nameof(PushAgent)}.{nameof(SyncSendAsync)}](Name: {this.Name}) sent msg {message.Size} bytes, typeHex({typeHex})");

                return x.MapErr(buffIoErr => new PushError(buffIoErr));
            }
            catch (Exception e)
            {
                log.Error($"[{nameof(PushAgent)}.{nameof(SyncSendAsync)}] exc: {e}");
                throw;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        public UniTask<Result<NUsize, PushError>> SendAsync(RxProxy<byte> rx, CancellationToken token = default)
            => throw new NotImplementedException();

        async UniTask<Result<NUsize, IPushError>> IPushAgent.SendAsync(RxProxy<byte> rx, CancellationToken token)
        {
            var x = await this.SendAsync(rx, token);
            return x.MapErr(e => (IPushError)e);
        }
    }

    public sealed class PushAgent<TItem> : IPushAgent<TItem>
    {
        private readonly PushAgent baseAgent_;

        private readonly AsyncMutex mutex_;

        public PushAgent(SessionTx sessionTx, Uri location, string name) : this(new(sessionTx, location, name))
        { }

        public PushAgent(PushAgent baseAgent)
        {
            this.baseAgent_ = baseAgent;
            this.mutex_ = new();
        }

        public Uri Location
            => this.baseAgent_.Location;

        async UniTask<Result<NUsize, IPushError>> IPushAgent.SendAsync(RxProxy<byte> rx, CancellationToken token)
        {
            var x = await this.baseAgent_.SendAsync(rx, token);
            return x.MapErr(e => (IPushError)e);
        }

        public async UniTask<Result<NUsize, IPushError>> EnqueueAsync(TItem item, CancellationToken token = default)
        {
            if (item is not TItem)
                throw new ArgumentNullException(paramName: nameof(item));

            Option<AsyncMutex.Guard> optGuard = Option.None();
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);

                var typeHex = item.GetType().FullName.GetStableHashCode();
                var jsonStr = JsonConvert.SerializeObject(item, PushConfig.DemoDefaultSettings);
                var jsonBin = Encoding.UTF8.GetBytes(jsonStr);

                if (jsonBin.Length > ushort.MaxValue)
                    throw new Exception($"instance (type: {typeof(TItem).Name}) is too larget (size: {jsonBin.Length}) to serialize");

                var sentRes = await this.baseAgent_.SyncSendAsync(typeHex, jsonBin, token);
                if (!sentRes.TryOk(out var len, out var err))
                    return Result.Err<IPushError>(err);

                return Result.Ok(len);
            }
            catch (Exception e)
            {
                Logger.Shared.Debug($"{e}");
                throw;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }
    }
}
