namespace RpcMuxSdk
{
    using System;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using NsAnyLR;
    using NsBufferKit;

    using LoggingSdk;

    public readonly struct PortBinderError
    {}

    public sealed class PortBinder<T> : IDisposable
    {
        private readonly SemaphoreSlim semaphore_;

        private readonly Port localPort_;

        private readonly SimpleMux<T> mux_;

        private bool isDisposed_;

        internal PortBinder(Port localPort, SimpleMux<T> mux)
        {
            this.semaphore_ = new(1, 1);
            this.localPort_ = LocalPort;
            this.mux_ = mux;
            this.isDisposed_ = false;
        }

        public Port LocalPort
            => this.localPort_;

        public async UniTask<Result<Listener<T>, PortBinderError>> GetListenerAsync(CancellationToken token = default)
        {
            bool succeeded = false;
            try
            {
                await this.semaphore_.WaitAsync(token);
                throw new NotImplementedException();
            }
            finally
            {
                if (!succeeded)
                    this.semaphore_.Release();
            }
        }

        public async UniTask<Result<Telegraph<T>, PortBinderError>> GetTelegraphAsync(CancellationToken token = default)
        {
            bool succeeded = false;
            try
            {
                await this.semaphore_.WaitAsync(token);
                throw new NotImplementedException();
            }
            finally
            {
                if (!succeeded)
                    this.semaphore_.Release();
            }
        }

        public async UniTask<Channel<T>> EstablishChannelAsync(Port remotePort, RxProxy<T> message, CancellationToken token = default)
        {
            bool succeeded = false;
            try
            {
                await this.semaphore_.WaitAsync(token);
                throw new NotImplementedException();
            }
            finally
            {
                if (!succeeded)
                    this.semaphore_.Release();
            }
        }

        #region IDisposable

        private void Dispose_(bool isDisposing)
        {
            if (this.isDisposed_)
                return;
            if (isDisposing)
            {

            }
            else
                Logger.Shared.Debug($"[{nameof(PortBinder<T>)}.{nameof(Dispose_)}] isDisposing: false");
        }

        public void Dispose()
            => this.Dispose_(true);

        ~PortBinder()
            => this.Dispose_(false);

        #endregion
    }
}
