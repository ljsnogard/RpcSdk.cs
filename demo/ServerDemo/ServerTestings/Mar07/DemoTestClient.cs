namespace ServerDemo.ServerTestings.Mar07
{
    using System;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using Cysharp.Threading.Tasks;

    using LoggingSdk;

    using Newtonsoft.Json;

    using BufferKit;

    using RpcPeerComSdk;
    using RpcClientSdk.Mar07;

    using LiquidRainbow.Mar07;

    public sealed class ClientParameters
    {
        public string Server { get; set; }

        public uint PlayerId { get; set; }

        public uint RoomCapcity { get; set; }

        public ClientParameters(string server, uint playerId, uint roomCapacity)
        {
            this.Server = server;
            this.PlayerId = playerId;
            this.RoomCapcity = roomCapacity;
        }
    }

    /// <summary>
    /// 用于模拟 demo 阶段客户端连接服务器，推送进入房间消息，拉取消息
    /// </summary>
    public sealed class DemoTestClient : IApp
    {
        /// <summary>
        /// 被外部调用的 app 接口，接收命令行中的 jsonArgs 参数解析为 <c>ClientParameters</c> 对象。
        /// </summary>
        public async UniTask RunAsync(string[] args, CancellationToken token = default)
        {
            var log = Logger.Shared;
            var jsonStr = args[0];
            var clientParams = JsonConvert.DeserializeObject<ClientParameters>(jsonStr);
            if (clientParams is not ClientParameters p)
                log.Debug($"Failed to parse json \"{jsonStr}\"");
            else
                await RunClientWithParameters(p);
        }

        private static async UniTask RunClientWithParameters(ClientParameters clientParameters)
        {
            var log = Logger.Shared;
            var serverEndp = clientParameters.Server.TryConvertToIPEndPoint();
            log.Debug($"server: {serverEndp}");
            var (pushAgent, pullAgent) = await EnterGameRoomAsync(
                serverEndp,
                clientParameters.PlayerId,
                clientParameters.RoomCapcity
            );
            UniTask? playerActTask = null;
            while (true)
            {
                try
                {
                    var maybeItem = await pullAgent.DequeueAsync();
                    if (!maybeItem.TryOk(out var message, out var pullErr))
                        throw pullErr.AsException();
                    switch (message)
                    {
                        case GameRoomEnter gameRoomEnter:
                            log.Debug($"[{nameof(DemoTestClient)}.{nameof(RunClientWithParameters)}] playerId({clientParameters.PlayerId}), recv: {nameof(GameRoomEnter)}: {gameRoomEnter}");
                            break;
                        case GameStartCountdown startCountDown:
                            log.Debug($"[{nameof(DemoTestClient)}.{nameof(RunClientWithParameters)}] playerId({clientParameters.PlayerId}), recv: {nameof(GameStartCountdown)}: {startCountDown}");
                            if (playerActTask is null && startCountDown.Signal == 1)
                                playerActTask = LoopSendFrameActAsync(clientParameters.PlayerId, pushAgent);
                            break;
                        case OneFrameInputsMessage frameActs:
                            log.Debug($"[{nameof(DemoTestClient)}.{nameof(RunClientWithParameters)}] playerId({clientParameters.PlayerId}), recv: {nameof(OneFrameInputsMessage)}: {frameActs}");
                            break;
                        default:
                            log.Error($"Unexepcted msg type: {message.GetType().FullName}");
                            break;
                    }
                }
                catch (SocketException se)
                {
                    log.Error($"{se}");
                    throw;
                }
                catch (Exception e)
                {
                    log.Error($"{e}");
                    break;
                }
            }
        }

        private static async UniTask LoopSendFrameActAsync(
            uint playerId,
            IPushAgent<IDemoPlayMessage> pushAgent,
            CancellationToken token = default)
        {
            var log = Logger.Shared;
            var gameStartTime = DateTimeOffset.Now;
            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    log.Debug($"[{nameof(DemoTestClient)}.{nameof(LoopSendFrameActAsync)}] playerId({playerId}) exits");
                    break;
                }
                try
                {
                    var now = DateTimeOffset.Now;
                    var rand = new Random(now.Millisecond);
                    var frame = (uint)((now - gameStartTime).TotalMilliseconds / 20);
                    var frameAct = new PlayerFrameAct(
                        id: playerId,
                        act: new FrameAct(frame: frame, input: CreateRandomInput(rand), bullet: CreateRandomBullet(rand))
                    );
                    var maybeCnt = await pushAgent.EnqueueAsync(frameAct, token);
                    if (!maybeCnt.TryOk(out var cnt, out var ioErr))
                        throw ioErr.AsException();
                    if (cnt != 1)
                        throw new Exception($"[{nameof(DemoTestClient)}.{nameof(LoopSendFrameActAsync)}] playerId({playerId}), unexpected push result({cnt})");
                    else
                        log.Debug($"[{nameof(DemoTestClient)}.{nameof(LoopSendFrameActAsync)}] playerId({playerId}), sent frameAct at frame {frameAct.Act.Frame}");

                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
                catch (SocketException se)
                {
                    log.Error($"{se}");
                    break;
                }
                catch (Exception e)
                {
                    log.Error($"{e}");
                }
            }

            static LRInputMessage CreateRandomInput(Random rand)
            {
                return new LRInputMessage(
                    move: new Coordinate2D<long>(rand.NextInt64(), rand.NextInt64()),
                    shot: new Coordinate2D<long>(rand.NextInt64(), rand.NextInt64()),
                    opt: rand.Next()
                );
            }

            static LRBulletMessage CreateRandomBullet(Random rand)
            {
                return new LRBulletMessage(
                    paintId: (uint)rand.Next(),
                    paint: new Coordinate3D<float>(rand.NextSingle(), rand.NextSingle(), rand.NextSingle()),
                    radius: rand.NextSingle(),
                    playerHp: (uint)rand.Next(),
                    frame: (uint)rand.Next(),
                    targetId: (uint)rand.Next()
                );
            }
        }

        /// <summary>
        /// 向服务器发起连接，推送一个指定 playerId 玩家进入房间的消息后，开始从服务端拉取房间内推送数据
        /// </summary>
        private static async UniTask<(IPushAgent<IDemoPlayMessage>, IPullAgent<IDemoPlayMessage>)> EnterGameRoomAsync(
            IPEndPoint server,
            uint playerId,
            uint roomCapacity,
            CancellationToken token = default)
        {
            NUsize SOCK_BUFF_SIZE = 64;

            var log = Logger.Shared;

            var socket = await BufferedSocket.ConnectAsync(server, token);
            var client = new DemoClient(socket);
            var pushAgent = new PushAgent<IDemoPlayMessage>(
                await client.PushAsync(Locations.GetPlayerPushLocation(roomCapacity, playerId), token));

            var pullAgent = new PullAgent<IDemoPlayMessage>(
                await client.PullAsync(Locations.GetGameRoomPullLocatioin(roomCapacity, playerId), token),
                Cysharp.Threading.Tasks.Channel.CreateSingleConsumerUnbounded<IDemoPlayMessage>
            );

            var dequeueRes = await pullAgent.DequeueAsync(token);
            if (!dequeueRes.IsOk(out var roomsListMsg))
                throw new Exception();

            if (roomsListMsg is not GameRoomListing roomsListing)
                throw new Exception($"Expecting message type of {typeof(GameRoomListing).FullName}, but got {roomsListMsg.GetType().FullName}");

            if (roomsListing.Listing.Any())
            {
                var sb = new StringBuilder();
                foreach (var info in roomsListing.Listing)
                {
                    var room = info.Value;
                    sb.Append($"RoomId: {info.Key}, Capacity: {room.Capacity}, Count: {room.Count}, UTime: {room.UTime}\n");
                }
                log.Info($"[{nameof(DemoTestClient)}.{nameof(EnterGameRoomAsync)}] game rooms list: \n{sb}");
            }
            var roomKv = roomsListing
                .Listing
                .ToList()
                .Where(kv => kv.Value.Count < kv.Value.Capacity);
            string roomId = string.Empty;
            string roomName = string.Empty;
            var playerName = string.Empty;
            if (roomKv.Any())
            {
                var kv = roomKv.First();
                roomId = kv.Key;
                roomName = kv.Value.RoomName;
            }
            var msg = new PlayerEnter(
                playerId: playerId,
                roomId: roomId,
                gameRoomCapacity: roomCapacity,
                playerName: playerName,
                roomName: roomName
            );
            var maybeCount = await pushAgent.EnqueueAsync(msg);
            if (!maybeCount.TryOk(out var msgCount, out var err) && msgCount == 1)
                throw new Exception();

            log.Debug($"[{nameof(DemoTestClient)}.{nameof(EnterGameRoomAsync)}] playerId({playerId}) sent {msgCount} bytes.");

            var basePullAgent = await client.PullAsync(Locations.GetGameRoomPullLocatioin(roomCapacity, playerId));
            return (pushAgent, pullAgent);
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