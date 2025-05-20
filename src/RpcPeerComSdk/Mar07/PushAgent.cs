namespace RpcPeerComSdk.Mar07
{
    using System;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using LoggingSdk;

    using BufferKit;
    using SerdesKit;

    static class PushConfig
    {
        public const int TYPE_HEX_SIZE = sizeof(uint);

        public const int JSON_BIN_SIZE = sizeof(ushort);
    }

    public readonly struct PushError: IPushError
    {
        public readonly IIoError InnerError;

        public PushError(IIoError innerError)
            => this.InnerError = innerError;

        public Exception AsException()
            => this.InnerError.AsException();
    }

    public sealed class PushAgent: IPushAgent
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

        internal ISerializer<TItem> GetSerializer<TItem>(OutputProxy<byte> output)
            => throw new NotImplementedException();

        internal OutputProxy<byte> Output
            => this.output_;

        internal AsyncMutex Mutex
            => this.mutex_;

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

        public PushAgent(OutputProxy<byte> output, Uri location, string name) : this(new(output, location, name))
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
            if (item is null)
                throw new ArgumentNullException(paramName: nameof(item));

            Option<AsyncMutex.Guard> optGuard = Option.None;
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Result.Ok(NUsize.Zero);

                throw new NotImplementedException();

                // return Result.Ok<NUsize>(1);
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
