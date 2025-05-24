namespace RpcClientSdk
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;

    using NsBufferKit;

    using RpcMuxSdk;

    /// <summary>
    /// 用于表示位于 EndPoint 上的某个服务器的连接，以及其所有复用的 channel
    /// </summary>
    public sealed class ClientSockConnection
    {
        private EndPoint remoteEndPoint_;

        private MuxAgreement muxAgreement_;

        private SimpleMux<byte>? smux_;

        private BufferedSocket? sockBuff_;

        internal ClientSockConnection(EndPoint remoteEndPoint, MuxAgreement muxAgreement)
        {
            this.remoteEndPoint_ = remoteEndPoint;
            this.muxAgreement_ = muxAgreement;
        }
    }
}