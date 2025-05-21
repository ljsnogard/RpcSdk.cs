namespace SerdesKit.MessagePack
{
    using System;
    using System.Threading;

    using Cysharp.Threading.Tasks;
    using BufferKit;

    public sealed class BufferedReader
    {
        private readonly RxProxy<byte> rx_;

        public BufferedReader(RxProxy<byte> rx)
            => this.rx_ = rx;
    }
}