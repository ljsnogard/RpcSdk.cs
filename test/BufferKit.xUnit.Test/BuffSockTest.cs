namespace BufferKit.xUnit.Test
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using Xunit.Abstractions;

    using Cysharp.Threading.Tasks;

    using BufferKit;

    [CollectionDefinition("Non-Parallel Collection", DisableParallelization = true)]
    public sealed class BufferedSocketTest
    {
        public BufferedSocketTest(ITestOutputHelper output)
            => Console.SetOut(new RedirectOutput(output));

        private static async Task<NUsize> HandleClientAsync(Socket clientSocket, CancellationToken token = default)
        {
            var bufferedSocket = await BufferedSocket.FromSocketAsync(
                createSocketAsync: (_) => UniTask.FromResult<Socket>(clientSocket),
                createTxBuffer: () => new RingBuffer<byte>(16),
                createRxBuffer: () => new RingBuffer<byte>(16),
                createTokenSource: () => new CancellationTokenSource(),
                token
            );
            var rx = bufferedSocket.GetCachedRxProxy();
            var tx = bufferedSocket.GetCachedTxProxy();
            // 执行回音操作
            var maybeLen = await tx.DumpAsync(rx, token);
            if (!maybeLen.TryOk(out var echoLen, out var txErr))
                throw txErr.AsException();
            return echoLen;
        }

        /// <summary>
        /// 用于测试 BufferedSocket.ConnectAsync 是否能正常连接到指定的 server 并进行数据收发。
        /// 数据收发调用了 <c>BuffSegmSocketExtensions.SendAsync</c> 和
        /// <c>BuffSegmSocketExtensions.ReceiveAsync</c>
        /// <seealso cref="BuffSegmSocketExtensions"/>
        /// </summary>
        [Fact]
        public async Task BufferedSocketConnectAsyncShouldWork()
        {
            var serverEndp = new IPEndPoint(IPAddress.Loopback, 2667);
            var listener = new TcpListener(serverEndp);
            listener.Start();
            var acceptTask = BuffSegmSocketExtensions.AcceptAsync(listener.Server);
            var (clientTx, clientRx) = await SocketIo.ConnectAsync(serverEndp);
            var acceptSock = await acceptTask;

            var clientTxProxy = clientTx.GetCachedProxy();
            var clientRxProxy = clientRx.GetCachedProxy();

            var (serverTx, serverRx) = SocketIo.Split(acceptSock);
            var serverTxProxy = serverTx.GetCachedProxy();
            var serverRxProxy = serverRx.GetCachedProxy();

            var greetings = "hello";
            var msgBytes = Encoding.UTF8.GetBytes(greetings);
            var clientWriteRes = await clientTxProxy.WriteAsync(msgBytes, CancellationToken.None);
            if (!clientWriteRes.TryOk(out var x , out var writeErr))
                throw writeErr.AsException();

            var buff = new Memory<byte>(new byte[x]);
            var serverReadRes = await serverRxProxy.ReadAsync(buff, CancellationToken.None);
            if (!serverReadRes.TryOk(out var y, out var readErr))
                throw readErr.AsException();
            Assert.Equal(x, y);
            Assert.Equal(greetings, Encoding.UTF8.GetString(buff.Span));
        }

        [Theory]
        [InlineData("01234", 1)]
        [InlineData("01234", 5)]
        [InlineData("01234", 16)]
        [InlineData("0123456789abcdef)!@#$%^&*(ABCDEF", 16)]
        public async Task BufferedSocketTxShouldWork(string message, int capacity)
        {
            var serverEndp = new IPEndPoint(IPAddress.Loopback, 2666 + capacity + message.Length);
            var serverCancelTokenSource = new CancellationTokenSource();
            var clientCancelTokenSource = new CancellationTokenSource();
            var serverStartedSignal = new TaskCompletionSource<bool>();

            // 启动一个 TCP 服务器用于发送回音
            var serverTask = RunServerAsync_(
                serverEndp,
                serverCancelTokenSource.Token
            );
            await serverStartedSignal.Task;

            // 启动一个 TCP 客户端发起通信
            var clientSocket = await BufferedSocket.ConnectAsync(
                serverEndp,
                bufferCapacity: (NUsize)capacity,
                token: CancellationToken.None
            );
            await Console.Out.WriteLineAsync($"[{nameof(BufferedSocketTest)}.{nameof(BufferedSocketTxShouldWork)}] connected to {serverEndp}");

            // 客户端发送消息到服务器，验证是否全部发送成功
            var srcBuff = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(message));
            var tx = clientSocket.GetCachedTxProxy();
            var maybeSentLen = await tx.DumpAsync(srcBuff, clientCancelTokenSource.Token);
            if (!maybeSentLen.TryOk(out var sentLen, out var txErr))
                throw txErr.AsException();
            Assert.Equal((NUsize)srcBuff.Length, sentLen);
            
            // 客户端接收服务器的回音
            var recvBuff = new Memory<byte>(new byte[srcBuff.Length]);
            var rx = clientSocket.GetCachedRxProxy();
            var maybeRecvLen = await rx.FillAsync(recvBuff, clientCancelTokenSource.Token);
            if (!maybeRecvLen.TryOk(out var recvLen, out var rxErr))
                throw rxErr.AsException();

            Assert.Equal(sentLen, recvLen);
            Assert.Equal(message, Encoding.UTF8.GetString(recvBuff.Span));

            // 用于保证服务器不会在执行到此行（测试完成）之前断开与客户端的连接
            await serverCancelTokenSource.CancelAsync();
            try
            {
                await serverTask;
            }
            catch (TaskCanceledException)
            { }
            return;

            async UniTask RunServerAsync_(
                IPEndPoint endpoint,
                CancellationToken token = default)
            {
                var listener = new TcpListener(endpoint);
                try
                {
                    listener.Start();
                }
                catch (Exception e)
                {
                    await Console.Out.WriteLineAsync($"{e} {e.Message}");
                    throw;
                }
                serverStartedSignal.TrySetResult(true);
                await Console.Out.WriteLineAsync($"server started at {endpoint}");

                var clientSockets = new LinkedList<(BufferedSocket, Task)>();
                while (true)
                {
                    if (token.IsCancellationRequested)
                        break;
                    try
                    {
                        var acceptSock = await BuffSegmSocketExtensions.AcceptAsync(listener.Server, token);
                        await Console.Out.WriteLineAsync($"server accepted client from {acceptSock.RemoteEndPoint}");

                        var buffSock = await BufferedSocket.FromSocketAsync(
                            tok => UniTask.FromResult<Socket>(acceptSock),
                            createTxBuffer: () => new RingBuffer<byte>((NUsize)capacity),
                            createRxBuffer: () => new RingBuffer<byte>((NUsize)capacity),
                            createTokenSource: () => new CancellationTokenSource(),
                            token
                        );
                        var sockRx = buffSock.GetCachedRxProxy();
                        var sockTx = buffSock.GetCachedTxProxy();
                        var echo = sockTx.DumpAsync(sockRx, token).AsTask();
                        clientSockets.AddLast((buffSock, echo));
                    }
                    catch (OperationCanceledException oce)
                    {
                        await Console.Out.WriteLineAsync($"[{nameof(RunServerAsync_)}] {oce}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        await Console.Out.WriteLineAsync($"[{nameof(RunServerAsync_)}] {ex}");
                        break;
                    }
                }
                foreach (var (client, echo) in clientSockets)
                {
                    await Console.Out.WriteLineAsync($"[{nameof(RunServerAsync_)}] disconnecting client from {client.RemoteEndPoint}");
                    client.Dispose();
                }
            }
        }
    }
}
