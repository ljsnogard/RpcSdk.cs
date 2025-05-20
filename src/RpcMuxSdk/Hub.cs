namespace RpcMuxSdk
{
    using System.Collections.Generic;

    internal sealed class Hub<T>
    {
        /// <summary>
        /// 以外部 port 为 Key 的已连接的 channel
        /// </summary>
        private readonly SortedDictionary<Port, Channel<T>> activeChannels_;

        /// <summary>
        /// 以外部 port 为 key 的半连接的 channel
        /// </summary>
        private readonly SortedDictionary<Port, Channel<T>> pendingChannels_;
    }
}
