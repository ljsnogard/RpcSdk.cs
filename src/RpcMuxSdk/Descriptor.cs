namespace RpcMuxSdk
{
    using System;

    public sealed class Descriptor<T> : IDisposable
    {
        private readonly Port localPort_;

        private readonly Port remotePort_;

        private readonly DateTimeOffset ctime_;

        private Channel<T>? channel_;

        private bool isDisposed_;

        public Descriptor(Port localPort, Port remotePort, Channel<T> channel)
        {
            this.localPort_ = localPort;
            this.remotePort_ = remotePort;
            this.ctime_ = DateTimeOffset.Now;
            this.channel_ = channel;
            this.isDisposed_ = false;
        }

        public Port LocalPort
            => this.localPort_;

        public Port RemotePort
            => this.remotePort_;

        public DateTimeOffset CreationTime
            => this.ctime_;

        #region IDisposable

        private void Dispose_(bool isDisposing)
        {
            if (this.isDisposed_)
                return;
            if (isDisposing)
            {
                try
                {

                }
                finally
                {
                    this.channel_ = null;
                    this.isDisposed_ = true;
                    GC.SuppressFinalize(this);
                }
            }
        }

        public void Dispose()
            => this.Dispose_(true);

        ~Descriptor()
            => this.Dispose_(false);

        #endregion
    }
}