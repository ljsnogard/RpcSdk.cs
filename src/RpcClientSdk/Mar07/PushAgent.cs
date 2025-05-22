namespace RpcClientSdk.Mar07
{
    using System;
    using System.Buffers.Binary;
    using System.Text;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using LoggingSdk;

    using Newtonsoft.Json;

    using BufferKit;

    using RpcPeerComSdk;

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
        private readonly OutputProxy<byte> output_;

        private readonly Uri location_;

        private readonly AsyncMutex mutex_;

        private readonly string name_;

        public PushAgent(OutputProxy<byte> output, Uri location, string name)
        {
            this.output_ = output;
            this.location_ = location;
            this.mutex_ = new();
            this.name_ = name;
        }

        public Uri Location
            => this.location_;

        public string Name
            => this.name_;

        internal OutputProxy<byte> Output
            => this.output_;

        internal AsyncMutex Mutex
            => this.mutex_;

        internal async UniTask<Result<NUsize, PushError>> SyncSendAsync(ReadOnlyMemory<byte> source, CancellationToken token = default)
        {
            var log = Logger.Shared;
            var acqRes = await this.mutex_.AcquireAsync(token);
            if (!acqRes.IsSome(out var guard))
                throw new Exception();
            try
            {
                var x = await this.Output.WriteAsync(source, token);
                if (x.TryOk(out var len, out var ioErr))
                    Logger.Shared.Debug($"[{nameof(PushAgent)}.{nameof(SyncSendAsync)}](Name: {this.Name}) sent msg {len} bytes");

                return x.MapErr(buffIoErr => new PushError(buffIoErr));
            }
            catch (Exception e)
            {
                log.Error($"[{nameof(PushAgent)}.{nameof(SyncSendAsync)}] exc: {e}");
                throw;
            }
            finally
            {
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

        private readonly SemaphoreSlim sema_;

        public PushAgent(OutputProxy<byte> output, Uri location, string name) : this(new(output, location, name))
        { }

        public PushAgent(PushAgent baseAgent)
        {
            this.baseAgent_ = baseAgent;
            this.sema_ = new(1, 1);
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

            try
            {
                await this.sema_.WaitAsync(token);

                var typeHex = item.GetType().FullName.GetStableHashCode();
                var jsonStr = JsonConvert.SerializeObject(item, PushConfig.DemoDefaultSettings);
                var jsonBin = Encoding.UTF8.GetBytes(jsonStr);

                if (jsonBin.Length > ushort.MaxValue)
                    throw new Exception($"instance (type: {typeof(TItem).Name}) is too larget (size: {jsonBin.Length}) to serialize");

                var prefixLen = PushConfig.TYPE_HEX_SIZE + PushConfig.JSON_BIN_SIZE;
                var srcLen = prefixLen + jsonBin.Length;
                var srcMem = new Memory<byte>(new byte[srcLen]);

                BinaryPrimitives.WriteUInt32BigEndian(srcMem.Span, typeHex);
                BinaryPrimitives.WriteUInt16BigEndian(srcMem.Slice(PushConfig.TYPE_HEX_SIZE, PushConfig.JSON_BIN_SIZE).Span, (ushort)jsonBin.Length);
#if DEBUG
                // try
                // {
                //     var prefixHexBuilder = new StringBuilder();
                //     for (var i = 0; i < prefixLen; ++i)
                //         prefixHexBuilder.Append($"{srcMem.Span[i]:X2} ");
                //     var prefixHex = prefixHexBuilder.ToString();

                //     Logger.Shared.Debug($"[{nameof(PushAgent<TItem>)}.{nameof(EnqueueAsync)}] typeHex({typeHex:X8}), jsonBin.Length({jsonBin.Length}, {jsonBin.Length:X4}), prefixHex: [\n{prefixHex}\n]");
                // }
                // finally
                // { }
#endif
                jsonBin.CopyTo(srcMem.Slice(prefixLen, jsonBin.Length));

                var maybeLen = await this.baseAgent_.SyncSendAsync(srcMem, token);
                if (!maybeLen.TryOk(out var len, out var err))
                    throw err.AsException();
                if (len != (NUsize)srcMem.Length)
                    throw new Exception($"Incomplete sent: {srcMem.Length} bytes to send but only {len} bytes done");

                try
                {
                    var jsonHexBuilder = new StringBuilder();
                    for (var i = 0; i < jsonBin.Length; ++i)
                        jsonHexBuilder.Append($"{jsonBin[i]:X2} ");
                    var jsonHex = jsonHexBuilder.ToString();

                    Logger.Shared.Debug($"[{nameof(PushAgent<TItem>)}.{nameof(EnqueueAsync)}] pushed {len} bytes, jsonStr({jsonStr}), jsonHex: [\n{jsonHex}\n]");
                }
                finally
                { }

                return Result.Ok<NUsize>(1);
            }
            catch (Exception e)
            {
                Logger.Shared.Debug($"{e}");
                throw;
            }
            finally
            {
                if (this.sema_.CurrentCount == 0)
                    this.sema_.Release();
            }
        }
    }
}
