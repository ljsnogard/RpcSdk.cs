namespace RpcMuxSdk
{
    using System;

    using NsAnyLR;
    using NsBufferKit;

    public readonly struct ChannelId: IEquatable<ChannelId>, IComparable<ChannelId>
    {
        public readonly Port LocalPort;

        public readonly Port RemotePort;

        public ChannelId(Port localPort, Port remotePort)
        {
            this.LocalPort = localPort;
            this.RemotePort = remotePort;
        }

        public bool Equals(ChannelId other)
            => this.LocalPort == other.LocalPort && this.RemotePort == other.RemotePort;

        public int CompareTo(ChannelId other)
        {
            var a = this.RemotePort.CompareTo(other.RemotePort);
            if (a == 0)
                return this.LocalPort.CompareTo(other.LocalPort);
            else
                return a;
        }

        public override string ToString()
            => $"{nameof(ChannelId)}(l: {this.LocalPort.code}, r: {this.RemotePort.code})";

        public override int GetHashCode()
            => HashCode.Combine(this.LocalPort, this.RemotePort);

        public override bool Equals(object obj)
        {
            if (obj is ChannelId other)
                return this.Equals(other);
            else
                return false;
        }
    }

    public readonly struct ChannelError
    {
        public readonly Result<RingBufferError, IIoError> InnerError;

        public ChannelError(Result<RingBufferError, IIoError> innerError)
            => this.InnerError = innerError;

        public static ChannelError FromBufferError(IIoError bufferError)
            => new ChannelError(Result<RingBufferError, IIoError>.Err(bufferError));

        public static ChannelError FromRingBufferError(RingBufferError ringBuffError)
            => new ChannelError(Result<RingBufferError, IIoError>.Err(ringBuffError));
    }

    public sealed class Channel<T>
    {
        private readonly Lazy<RingBuffer<T>> lazyChanTx_;

        /// <summary>
        /// 从 Mux 中接收的数据的缓冲，已去除头部信息，即只有报文中的 Body 部分
        /// </summary>
        private readonly Lazy<RingBuffer<T>> lazyChanRx_;

        internal TxProxy<T> RxWriter
            => this.lazyChanRx_.Value.GetCachedTxProxy();

        internal RxProxy<T> TxReader
            => this.lazyChanTx_.Value.GetCachedRxProxy();

        public Channel(
            Func<RingBuffer<T>> createTxBuff,
            Func<RingBuffer<T>> createRxBuff)
        {
            this.lazyChanTx_ = new Lazy<RingBuffer<T>>(createTxBuff);
            this.lazyChanRx_ = new Lazy<RingBuffer<T>>(createRxBuff);
        }

        /// <summary>
        /// 频道数据的生产端（发送端）
        /// </summary>
        public TxProxy<T> Tx
            => this.lazyChanTx_.Value.GetCachedTxProxy();

        /// <summary>
        /// 频道的消费端（接收端）
        /// </summary>
        public RxProxy<T> Rx
            => this.lazyChanRx_.Value.GetCachedRxProxy();
    }
}
