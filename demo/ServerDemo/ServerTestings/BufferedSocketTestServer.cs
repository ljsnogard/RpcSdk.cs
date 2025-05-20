namespace ServerDemo.ServerTestings
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;

    using BufferKit;

    using Cysharp.Threading.Tasks;

    using LoggingSdk;

    public sealed class BufferedSocketTestServer : IApp
    {
        public UniTask RunAsync(string[] args, CancellationToken token = default)
            => RunEchoServerAsync(new IPEndPoint(IPAddress.Any, 2667), 16, token);

        /// <summary>
        /// 用于与客户端联调测试，回音服务器
        /// </summary>
        public static async UniTask RunEchoServerAsync(
            IPEndPoint endpoint,
            NUsize capacity,
            CancellationToken token = default)
        {
            var listener = new TcpListener(endpoint);
            try
            {
                listener.Start();
            }
            catch (Exception e)
            {
                Logger.Shared.Debug($"{e} {e.Message}");
                throw;
            }
            Logger.Shared.Debug($"server started at {endpoint}");

            var clientSockets = new LinkedList<(BufferedSocket, Task)>();
            while (true)
            {
                if (token.IsCancellationRequested)
                    break;
                try
                {
                    var acceptSock = await BuffSegmSocketExtensions.AcceptAsync(listener.Server, token);
                    Logger.Shared.Debug($"server accepted client from {acceptSock.RemoteEndPoint}");

                    var buffSock = await BufferedSocket.FromSocketAsync(
                        tok => UniTask.FromResult(acceptSock),
                        createTxBuffer: () => new RingBuffer<byte>(capacity),
                        createRxBuffer: () => new RingBuffer<byte>(capacity),
                        createTokenSource: () => new CancellationTokenSource(),
                        token
                    );
                    var sockRx = buffSock.GetCachedRxProxy();
                    var sockTx = buffSock.GetCachedTxProxy();
                    var echo = sockTx.DumpAsync(sockRx, token).AsTask();
                    clientSockets.AddLast((buffSock, echo));
                }
                catch (Exception ex)
                {
                    Logger.Shared.Debug($"[{nameof(RunEchoServerAsync)}] {ex}");
                    break;
                }
            }
            foreach (var (client, echo) in clientSockets)
            {
                Logger.Shared.Debug($"[{nameof(RunEchoServerAsync)}] disconnecting client from {client.RemoteEndPoint}");
                client.Dispose();
            }
        }
    }
}
