namespace S2_ServerDemo
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http.Headers;
    using System.Net.Sockets;
    using System.Runtime.CompilerServices; // for EnumerationCancellationAttributes
    using System.Threading;
    using System.Threading.Tasks;

    using Cysharp.Threading.Tasks;

    using LoggingSdk;

    using Newtonsoft.Json;

    using NsAnyLR;
    using NsBufferKit;

    using RpcClientSdk.Mar07;

    public sealed class ServerParameters
    {
        public string ServerEndpoint { get; set; }

        /// <summary>
        /// 每秒逻辑帧数量
        /// </summary>
        public uint Fps { get; set; }

        /// <summary>
        /// 游戏对局最大时长，单位为秒
        /// </summary>
        public uint GameDuration { get; set; }

        public ServerParameters(string serverEndpoint, uint fps, uint gameDuration = 360)
        {
            this.ServerEndpoint = serverEndpoint;
            this.Fps = fps;
            this.GameDuration = gameDuration;
        }
    }

    public sealed class LoginDemoServer : IApp
    {
        public UniTask RunAsync(string[] args, CancellationToken token = default)
        {
            var parameters = JsonConvert.DeserializeObject<ServerParameters>(args[0]);
            if (parameters is not ServerParameters p)
                throw new ArgumentException($"Unable to parse to json from args[0]: \"{args[0]}\"");
            return this.RunServerAsync(p, token);
        }

        private async UniTask RunServerAsync(ServerParameters p, CancellationToken token = default)
        {
            var log = Logger.Shared;
            var server = p.ServerEndpoint.TryConvertToIPEndPoint();
            var listener = new TcpListener(server);
            try
            {
                listener.Start();
                log.Info($"[{nameof(LoginDemoServer)}.{nameof(RunServerAsync)}] listener started at: {server}, fps: {p.Fps}, game duration: {p.GameDuration}");
                await this.LoopAcceptAsync_(listener.Server, p, this.HandleClientAsync, token);
            }
            catch (OperationCanceledException)
            {
                log.Warn($"DemoServer({server}) received cancel signal");
            }
        }

        /// <summary>
        /// 在一个独立的 context 中启动 accept 客户端的循环
        /// </summary>
        private async UniTask LoopAcceptAsync_
            (Socket listener
            , ServerParameters serverParameters
            , Func<Socket, Task> handleClientAysnc
            , CancellationToken token = default)
        {
            var log = Logger.Shared;
            var mutex = new AsyncMutex();
            LinkedList<Task> clientTasks = new();
            while (true)
            {
                try
                {
                    var client = await BuffSegmSocketExtensions.AcceptAsync(listener);
                    log.Info($"[{nameof(LoginDemoServer)}.{nameof(LoopAcceptAsync_)}] accepted client from {client.RemoteEndPoint}");
                    var handlingTask = handleClientAysnc(client);

                    Option<AsyncMutex.Guard> optGuard = Option.None();
                    try
                    {
                        optGuard = await mutex.AcquireAsync();
                        if (!optGuard.IsSome(out var guard))
                            continue;
                        clientTasks.AddLast(handlingTask);
                        if (clientTasks.Count > 64)
                        {
                            var head = clientTasks.First;
                            while (true)
                            {
                                if (head is not LinkedListNode<Task> curr)
                                    break;
                                if (curr.Value is not Task handledTask)
                                {
                                    head = curr.Next;
                                    clientTasks.Remove(curr);
                                    continue;
                                }
                                if (handledTask.IsCompleted || handledTask.IsCanceled)
                                {
                                    head = curr.Next;
                                    clientTasks.Remove(curr);
                                    continue;
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (optGuard.IsSome(out var guard))
                            guard.Dispose();
                    }
                }
                catch (OperationCanceledException)
                {
                    log.Debug($"DemoServer({listener.LocalEndPoint}) RunPrepareAsync_ received cancel signal");
                    break;
                }
            }
        }

        private Task HandleClientAsync(Socket client)
        {
            return Task.CompletedTask;
        }
    }

    public static class StringEndpointConvertExtensions
    {
        public static IPEndPoint TryConvertToIPEndPoint(this string endpointString)
        {
            // 按冒号拆分字符串
            var parts = endpointString.Split(':');
            if (parts.Length != 2)
                throw new ArgumentException($"无法解析地址 {endpointString}");

            var ipAddrStr = parts[0];
            var portStr = parts[1];
            // 解析 IP 地址
            var ipAddress = IPAddress.Parse(ipAddrStr);
            // 解析端口号
            if (int.TryParse(portStr, out int port) && port >= IPEndPoint.MinPort && port <= IPEndPoint.MaxPort)
                // 创建 IPEndPoint 对象
                return new IPEndPoint(ipAddress, port);
            else
                throw new ArgumentException($"无效的端口字符串 \"{portStr}\"");
        }
    }
}