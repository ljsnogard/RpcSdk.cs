namespace RpcMuxSdk
{
    public sealed class Listener<T>
    {
        private readonly PortBinder<T> portBinder_;

        internal Listener(PortBinder<T> portBinder)
        {
            this.portBinder_ = portBinder;
        }

        public Port LocalPort
            => this.portBinder_.LocalPort;
    }
}
