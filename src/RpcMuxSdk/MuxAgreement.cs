namespace RpcMuxSdk
{
    using System;

    public readonly struct MuxAgreement
    {
        /// <summary>
        /// 单个 Packet 的最大长度
        /// </summary>
        public readonly UInt32 MaxPacketSize;

        /// <summary>
        /// 一个 Channel 在无任何数据交流后的最长存活时间，单位为秒。
        /// </summary>
        /// <remarks>
        /// 不断地发送心跳报文（ACK）可以无限地延长 channel 存活时间，直到有一端主动关闭。
        /// </remarks>
        public readonly UInt16 ChannelTimeout;

        /// <summary>
        /// 接收一个 Packet 的不同段落时的超时时间，单位为秒。
        /// </summary>
        /// <remarks>
        /// 接收一个 Packet 的总耗费时间可以远大于 Packet Timeout。
        /// 例如，Packet 中的每个字节之间的间隔都恰好小于 Packet Timeout 的时间。
        /// </remarks>
        public readonly UInt16 PacketTimeout;
    }
}
