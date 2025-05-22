namespace ServerDemo.ServerTestings.Mar07
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Sockets;
    using System.Runtime.CompilerServices; // for EnumerationCancellationAttributes
    using System.Threading;
    using System.Threading.Tasks;

    using BufferKit;
    using Cysharp.Threading.Tasks;
    using LiquidRainbow.Mar07;
    using LoggingSdk;
    using Newtonsoft.Json;

    using RpcClientSdk.Mar07;

    using PlayerId = System.UInt32;

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

    public sealed class DemoServer : IApp
    {
        private readonly CancellationTokenSource prepareStageCts_;

        private readonly ConcurrentDictionary<string, GameRoom> gameRooms_;

        private DateTimeOffset lastCleanTime_;

        public DemoServer()
        {
            this.prepareStageCts_ = new CancellationTokenSource();
            this.gameRooms_ = new ConcurrentDictionary<string, GameRoom>();
            this.lastCleanTime_ = DateTimeOffset.MinValue;
        }

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
                log.Info($"[{nameof(DemoServer)}.{nameof(RunServerAsync)}] listener started at: {server}, fps: {p.Fps}, game duration: {p.GameDuration}");
                await this.LoopAcceptAsync_(listener.Server, p.Fps, p.GameDuration, token);
            }
            catch (OperationCanceledException)
            {
                log.Warn($"DemoServer({server}) received cancel signal");
            }
        }

        /// <summary>
        /// 在一个独立的 context 中启动 accept 客户端的循环
        /// </summary>
        private async UniTask LoopAcceptAsync_(Socket listener, uint fps, uint gameDurationSeconds, CancellationToken token = default)
        {
            var log = Logger.Shared;
            List<Task> clientTasks = new();
            while (true)
            {
                try
                {
                    var client = await BuffSegmSocketExtensions.AcceptAsync(listener, this.prepareStageCts_.Token);
                    log.Info($"[{nameof(DemoServer)}.{nameof(LoopAcceptAsync_)}] accepted client from {client.RemoteEndPoint}");
                    var handling = this.HandleClientAsync_(client, fps, gameDurationSeconds, token);
                    clientTasks.Add(handling);
                }
                catch (OperationCanceledException)
                {
                    log.Debug($"DemoServer({listener.LocalEndPoint}) RunPrepareAsync_ received cancel signal");
                    break;
                }
            }
        }

        /// <summary>
        /// 最后一个连上的客户端启动 Countdown
        /// </summary>
        private async Task HandleClientAsync_(
            Socket client,
            uint fps,
            uint gameDurationSeconds,
            CancellationToken token = default)
        {
            var log = Logger.Shared;
            try
            {
                var (gameRoom, player, playersCount) = await SelectGameRoomAsync(client, fps, token);
                var gameLoopTask = gameRoom.StartGameRoomLoop(player, fps, gameDurationSeconds);
                if (gameRoom.Capacity == playersCount)
                {
                    var succ = await gameRoom.TryStartCountDownAsync(token);
                    if (!succ)
                        throw new Exception();
                }
                log.Info($"[{nameof(DemoServer)}.{nameof(HandleClientAsync_)}] player(Id: {player.Id}) entered game room({gameRoom.RoomName}, id: {gameRoom.RoomId})");
                await gameLoopTask;
            }
            catch (Exception e)
            {
                log.Error($"{e}");
                throw;
            }

            /// <summary>
            /// 等待玩家推送一个 PlayerEnter 并绑定玩家和游戏房间。
            /// </summary>
            async UniTask<(GameRoom, Player, NUsize)> SelectGameRoomAsync(Socket client, uint fps, CancellationToken token = default)
            {
                var (sock_out, sock_in) = SocketIo.Split(client);

                var output = sock_out.GetCachedProxy();
                var input = sock_in.GetCachedProxy();

                await ListingGameRoomsAsync(output, token);

                var pullAgent = new PullAgent<IDemoPlayMessage>(
                    // 在这个 demo 里我们实际上并不需要真的关心客户端要推送到哪一个资源，所以可以随便给一个 Location
                    // 但是将来必须解决可能出现的相对路径问题。
                    new PullAgent(
                        input,
                        location: new Uri($"rpc://lr.demo/Pull{client.RemoteEndPoint:X8}"),
                        name: $"(l: {client.LocalEndPoint}, r: {client.RemoteEndPoint})"
                    ),
                    () => Cysharp.Threading.Tasks.Channel.CreateSingleConsumerUnbounded<IDemoPlayMessage>()
                );
                var maybeMessage = await pullAgent.DequeueAsync(token);
                if (!maybeMessage.TryOk(out var demoPlayMessage, out var pullErr))
                    throw pullErr.AsException();

                if (demoPlayMessage is not PlayerEnter playerEnter)
                    throw new Exception($"[{nameof(DemoServer)}.{nameof(SelectGameRoomAsync)}] 客户端({client.RemoteEndPoint}) 推送了 {demoPlayMessage.GetType()} 类型的消息，而不是 {typeof(PlayerEnter)} 类型");

                var playerId = playerEnter.PlayerId;
                var playerName = playerEnter.PlayerName;
                var roomId = playerEnter.RoomId;
                var roomName = playerEnter.RoomName;
                var roomCapacity = playerEnter.GameRoomCapacity;

                if (string.IsNullOrEmpty(playerName) || string.IsNullOrWhiteSpace(playerName))
                    playerName = $"来自【{client.RemoteEndPoint}】的玩家";

                if (string.IsNullOrEmpty(roomName) || string.IsNullOrWhiteSpace(roomName))
                    roomName = $"【{playerName}】的房间";

                // 若客户端所推送的消息不携带 game room id 则用 hashcode ，碰撞概率应该不高
                if (string.IsNullOrEmpty(roomId))
                {
                    var hash = unchecked((uint)HashCode.Combine(DateTimeOffset.Now, roomName));
                    roomId = $"{hash:x8}";
                }
                // 添加或者查找一个已有的房间
                var maybeGameRoom = this.gameRooms_.GetOrAdd(roomId, id => new GameRoom(id, roomName, roomCapacity, fps));
                if (maybeGameRoom is not GameRoom gameRoom)
                    throw new Exception($"[{nameof(DemoServer)}.{nameof(SelectGameRoomAsync)}] server error: 无法添加或找到房间 (roomCapacity({roomCapacity}))");
                if (gameRoom.Capacity != roomCapacity)
                    throw new Exception($"[{nameof(DemoServer)}.{nameof(SelectGameRoomAsync)}] server error: 要加入房间的 capacity 为 {gameRoom.Capacity}，与参数({roomCapacity}) 不符");

                // 必须让这个 pullAgent 先停止工作，由 player 对象内部的 pullAgent 接替，否则会出 Bug
                await pullAgent.StopAsync();

                var player = new Player(client, output, input, roomCapacity, playerId);
                var maybeSucc = await gameRoom.TryAddPlayerAsync(player, playerName);
                if (!maybeSucc.TryOk(out var pc0, out var pc1))
                    throw new Exception($"[{nameof(DemoServer)}.{nameof(SelectGameRoomAsync)}] server error: 加入房间失败");

                return (gameRoom, player, pc0);
            }

            async UniTask ListingGameRoomsAsync(OutputProxy<byte> output, CancellationToken token = default)
            {
                // 游戏房间最大空闲时间
                TimeSpan MAX_GAMEROOM_LIFE = TimeSpan.FromMinutes(5);

                // 清扫最大时间间隔
                TimeSpan MAX_CLEAN_INTERVAL = TimeSpan.FromMinutes(2);

                var log = Logger.Shared;
                while (true)
                {
                    try
                    {
                        var kvList = this.gameRooms_.ToList();
                        var deadline = DateTimeOffset.Now;
                        if (deadline - this.lastCleanTime_ >= MAX_CLEAN_INTERVAL)
                        {
                            var outdatedRoomsIds =
                                from kv in kvList
                                where deadline - kv.Value.UTime >= MAX_GAMEROOM_LIFE
                                select kv.Key;
                            foreach (var k in outdatedRoomsIds)
                                this.gameRooms_.Remove(k, out _);

                            this.lastCleanTime_ = deadline;
                            log.Debug($"[{nameof(DemoServer)}.{nameof(ListingGameRoomsAsync)}] removed {outdatedRoomsIds.Count()} outdated game rooms.");
                            continue;
                        }
                        var infoList =
                            from kv in kvList
                            orderby kv.Value.UTime descending
                            select KeyValuePair.Create(
                                kv.Key,
                                new GameRoomListingInfo
                                {
                                    RoomName = kv.Value.RoomName,
                                    Capacity = kv.Value.Capacity,
                                    Count = kv.Value.PlayersCount,
                                    UTime = kv.Value.UTime
                                }
                            );
                        var pushAgent = new PushAgent<IDemoPlayMessage>(
                            output,
                            location: new Uri($"rpc://lr.demo/Push{client.RemoteEndPoint:X8}"),
                            name: $"(l: {client.LocalEndPoint}, r: {client.RemoteEndPoint})"
                        );
                        var listing = new GameRoomListing(new Dictionary<string, GameRoomListingInfo>(infoList));
                        var enqueueResult = await pushAgent.EnqueueAsync(listing);
                        break;
                    }
                    catch (Exception e)
                    {
                        log.Error($"[{nameof(DemoServer)}.{nameof(ListingGameRoomsAsync)}] {e}");
                    }
                }
            }
        }
    }

    internal sealed class Player
    {
        public sealed class Comparer: IComparer<Player>
        {
            public static readonly Comparer Shared = new();

            public int Compare(Player? a, Player? b)
            {
                if (a is Player p1 && b is Player p2)
                    return p1.Id.CompareTo(p2.Id);
                else
                    throw new ArgumentNullException();
            }
        }

        private readonly uint roomCapacity_;

        private readonly Socket socket_;

        public Socket Socket
            => this.socket_;

        public InputProxy<byte> Input { get; }

        public OutputProxy<byte> Output { get; }

        public PlayerId Id { get; }

        /// <summary>
        /// 推送给客户端的消息队列
        /// </summary>
        public System.Threading.Channels.Channel<IDemoPlayMessage> ServerPushQueue
            => this.serverPushQueue_;

        public PullAgent<IDemoPlayMessage> PlayerPushed
            => this.playerPushAgent_;

        public CancellationTokenSource TokenSource { get; }

        private readonly System.Threading.Channels.Channel<IDemoPlayMessage> serverPushQueue_;

        private readonly Task playerTxLoopTask_;

        private readonly PullAgent<IDemoPlayMessage> playerPushAgent_;

        public Player
            ( Socket socket
            , OutputProxy<byte> output
            , InputProxy<byte> input
            , uint roomCapacity
            , PlayerId id)
        {
            this.roomCapacity_ = roomCapacity;
            this.socket_ = socket;
            this.Output = output;
            this.Input = input;
            this.Id = id;
            this.serverPushQueue_ = System.Threading.Channels.Channel.CreateUnbounded<IDemoPlayMessage>();
            this.TokenSource = new CancellationTokenSource();
            this.playerTxLoopTask_ = this.ConsumeServerPushLoopAsync_();
            this.playerPushAgent_ = new PullAgent<IDemoPlayMessage>(
                input,
                location: Locations.GetPlayerPushLocation(roomCapacity, id),
                name: $"(l: {socket.LocalEndPoint}, r: {socket.RemoteEndPoint})",
                () => Cysharp.Threading.Tasks.Channel.CreateSingleConsumerUnbounded<IDemoPlayMessage>()
            );
        }

        /// <summary>
        /// 循环地消费 serverPushQueue_ （由外部 Enqueue）的内容，将内容通过 socket 发送出去
        /// </summary>
        private async Task ConsumeServerPushLoopAsync_()
        {
            var log = Logger.Shared;
            var pushAgent = new PushAgent<IDemoPlayMessage>(
                new PushAgent(
                    this.Output,
                    location: Locations.GetPlayerPushLocation(this.roomCapacity_, this.Id),
                    name: $"(l: {this.Socket.LocalEndPoint}, r: {this.Socket.RemoteEndPoint})"
            ));
            log.Debug($"[{nameof(Player)}.{nameof(ConsumeServerPushLoopAsync_)}] player(Id: {this.Id}) start tx loop");

            var serverPushed = this.serverPushQueue_.Reader;
            var token = this.TokenSource.Token;
            while (!token.IsCancellationRequested)
            {
                var msg = await serverPushed.ReadAsync(token);
                log.Debug($"[{nameof(Player)}.{nameof(ConsumeServerPushLoopAsync_)}] player(id: {this.Id}, ep: {this.Socket.RemoteEndPoint}) dequeue msg({msg.GetType().FullName})");

                var maybeLen = await pushAgent.EnqueueAsync(msg, token);
                if (!maybeLen.TryOk(out var itemCount, out var pushErr))
                    throw pushErr.AsException();
                if (itemCount != 1)
                    throw new Exception($"unexpected enqueue result: {itemCount}");

                log.Debug($"[{nameof(Player)}.{nameof(ConsumeServerPushLoopAsync_)}] player(id: {this.Id}, ep: {this.Socket.RemoteEndPoint}) dequeue msg({msg.GetType().FullName})");
            }
        }
    }

    internal readonly struct FrameTick : IDemoPlayMessage
    {
        public readonly uint Tick;

        public FrameTick(uint tick)
            => this.Tick = tick;
    }

    internal sealed class GameRoom
    {
        public const uint COUNTDOWN_TOTAL_SECONDS = 3;

        private readonly string roomId_;

        private readonly string roomName_;

        /// <summary>
        /// 游戏房间创建时间
        /// </summary>
        private readonly DateTimeOffset ctime_;

        /// <summary>
        /// 已进入的玩家
        /// </summary>
        private readonly SortedSet<Player> players_;

        private readonly SemaphoreSlim sema_;

        /// <summary>
        /// 可以容纳多少玩家
        /// </summary>
        private readonly uint capacity_;

        private readonly uint fps_;

        private readonly TaskCompletionSource<DateTimeOffset> countDownStartedTime_;

        private readonly Cysharp.Threading.Tasks.Channel<IDemoPlayMessage> collectMq_;

        private readonly Cysharp.Threading.Tasks.Channel<IDemoPlayMessage> dispatchMq_;

        private Task? frameTicksTask_;

        private Task? loopTask_;

        public GameRoom
            ( string roomId
            , string roomName
            , uint capacity
            , uint fps)
        {
            this.roomId_ = roomId;
            this.roomName_ = roomName;
            this.players_ = new SortedSet<Player>(Player.Comparer.Shared);
            this.sema_ = new SemaphoreSlim(1, 1);
            this.capacity_ = capacity;
            this.ctime_ = DateTimeOffset.Now;
            this.UTime = this.ctime_;
            this.fps_ = fps;
            this.countDownStartedTime_ = new TaskCompletionSource<DateTimeOffset>();
            this.collectMq_ = Cysharp.Threading.Tasks.Channel.CreateSingleConsumerUnbounded<IDemoPlayMessage>();
            this.dispatchMq_ = Cysharp.Threading.Tasks.Channel.CreateSingleConsumerUnbounded<IDemoPlayMessage>();
            this.frameTicksTask_ = null;
            this.loopTask_ = null;
        }

        public string RoomId
            => this.roomId_;

        public string RoomName
            => this.roomName_;

        public uint Capacity
            => this.capacity_;

        public uint PlayersCount
            => unchecked((uint)this.players_.Count);

        public DateTimeOffset CTime
            => this.ctime_;

        /// <summary>
        /// 游戏房间最后更新时间
        /// </summary>
        public DateTimeOffset UTime { get; set; }

        public bool IsCoutdownStarted
            => this.countDownStartedTime_.Task.IsCompleted;

        /// <summary>
        /// 尝试将一个 Player 添加到游戏房间，如果游戏房间人数已达到设定的上限，则加入会失败。
        /// 加入成功后，会创建一个 <c>GameRoomEnter</c> 并放入 <c>collectMq_</c>。
        /// </summary>
        /// <remarks>此方法线程安全，且可以重复调用，因为调用时会先尝试获取内部 Semaphore。</remarks>
        public async UniTask<Result<NUsize, NUsize>> TryAddPlayerAsync(
            Player player,
            string playerName,
            CancellationToken token = default)
        {
            try
            {
                await this.sema_.WaitAsync(token);
                if (!this.players_.Add(player))
                    return Result.Err((NUsize)this.players_.Count);

                var players =
                    from p in this.players_
                    select new KeyValuePair<PlayerId, string>(p.Id, playerName);
                var d = new Dictionary<PlayerId, string>();
                foreach (var kv in players)
                    d.Add(kv.Key, kv.Value);

                var m = new GameRoomEnter(d, player.Id, this.roomId_, this.Capacity);
                var collectMq = this.collectMq_.Writer;
                var isTryWriteSucc = collectMq.TryWrite(m);
                if (!isTryWriteSucc)
                    throw new Exception();

                this.UTime = DateTimeOffset.Now;
                return Result.Ok((NUsize)this.players_.Count);
            }
            finally
            {
                if (this.sema_.CurrentCount == 0)
                    this.sema_.Release();
            }
        }

        public Task CollectPlayerMessageAsync(Player player, CancellationToken token = default)
            => GameRoom.LoopCollectPlayerMessagesAsync(player, this.collectMq_.Writer, token);

        /// <summary>
        /// 尝试将一个游戏房间设置为已开始倒数。
        /// </summary>
        /// <returns>当且仅当游戏房间人数刚好为上限，且之前未设置为开始，才会返回 true，否则返回 false </returns>
        public async UniTask<bool> TryStartCountDownAsync(CancellationToken token = default)
        {
            try
            {
                await this.sema_.WaitAsync(token);
                if (this.IsCoutdownStarted)
                    return false;
                if ((NUsize)this.players_.Count != this.Capacity)
                    return false;
                return this.countDownStartedTime_.TrySetResult(DateTimeOffset.Now);
            }
            finally
            {
                if (this.sema_.CurrentCount == 0)
                    this.sema_.Release();
            }
        }

        public async Task StartGameRoomLoop(Player player, uint fps, uint gameDurationSeconds)
        {
            var log = Logger.Shared;
            try
            {
                await this.sema_.WaitAsync();
                if (this.loopTask_ is null)
                    this.loopTask_ = RunGameRoomProcessAsync(default);
            }
            finally
            {
                if (this.sema_.CurrentCount == 0)
                    this.sema_.Release();
            }
            await this.CollectPlayerMessageAsync(player, default);
            await this.loopTask_;

            async Task RunGameRoomProcessAsync(CancellationToken tok)
            {
                var dispatchTask = this.DispatchMessageAsync(tok);
                var countDownStartTime = await this.WaitGameEnterAsync(tok);
                log.Debug($"[{nameof(GameRoom)}.{nameof(StartGameRoomLoop)}] count down started");

                this.frameTicksTask_ = this.YieldTicksAsync(
                    startTime: countDownStartTime,
                    ticksPerSecond: this.fps_,
                    maxTickCount: (GameRoom.COUNTDOWN_TOTAL_SECONDS + gameDurationSeconds + 1) * this.fps_,
                    tok
                );
                var yieldCountDown = this.YieldCountDownAsync(
                    countdownBegin: GameRoom.COUNTDOWN_TOTAL_SECONDS,
                    ticksPerSecond: this.fps_,
                    tok
                );
                await yieldCountDown;
                await this.YieldAggrFrameMessageAsync(
                    collectMq: this.collectMq_.Reader,
                    dispatchMq: this.dispatchMq_.Writer,
                    fps: this.fps_,
                    roomCapacity: this.capacity_,
                    tok
                );
                await dispatchTask;
            }
        }

        /// <summary>
        /// 监听 <c>collectMq_</c> 等待所有玩家进入 GameRoom，在等待过程中会向已进入玩家推送事件通知
        /// </summary>
        private async Task<DateTimeOffset> WaitGameEnterAsync(CancellationToken token = default)
        {
            var log = Logger.Shared;
            log.Debug($"[{nameof(GameRoom)}.{nameof(WaitGameEnterAsync)}] (Capacity: {this.Capacity}, PC: {this.players_.Count}) start loop.");
            try
            {
                var collectMqRx = this.collectMq_.Reader;
                var dispatchMqTx = this.dispatchMq_.Writer;
                while (true)
                {
                    if (this.countDownStartedTime_.Task.IsCompleted)
                        break;
                    var maybeMsg = await collectMqRx.ReadAsync(token);
                    if (maybeMsg is not IDemoPlayMessage msg)
                        throw new Exception("collectMqRx.ReadAsync failed");
                    if (msg is not GameRoomEnter gre)
                        continue;

                    var isTryWriteSucc = dispatchMqTx.TryWrite(gre);
                    if (!isTryWriteSucc)
                        throw new Exception("ispatchMqTx.TryWrite failed");

                    // 如果已经有足够数量的玩家进入游戏房间，就可以退出等待阶段，进入倒数读秒阶段
                    if (this.Capacity == this.players_.Count)
                    {
                        log.Debug($"[{nameof(GameRoom)}.{nameof(WaitGameEnterAsync)}] (Capacity: {this.Capacity}, PC: {this.players_.Count}) exiting loop.");
                        break;
                    }
                    else
                        continue;
                }
                return await this.countDownStartedTime_.Task;
            }
            catch (Exception e)
            {
                log.Error($"{e}");
                throw;
            }
        }

        private async Task YieldTicksAsync(
            DateTimeOffset startTime,
            uint ticksPerSecond,
            uint maxTickCount,
            CancellationToken token = default)
        {
            var log = Logger.Shared;

            var tickTimeSpan = TimeSpan.FromSeconds(1) / ticksPerSecond;
            uint tickOffset = 1;
            var delta = TimeSpan.FromMilliseconds(1);

            var collectMqTx = this.collectMq_.Writer;

            log.Debug($"[{nameof(GameRoom)}.{nameof(YieldTicksAsync)}] startTime: {startTime}, ticksPerSecond: {ticksPerSecond}, maxTickCount: {maxTickCount}");

            while (tickOffset < maxTickCount)
            {
                try
                {
                    if (token.IsCancellationRequested)
                        break;

                    var now = DateTimeOffset.Now;
                    var expectNextFrameTime = startTime + (tickTimeSpan * tickOffset);
                    var nextFrameSpan = expectNextFrameTime - now - delta;
                    if (expectNextFrameTime - delta > now)
                    {
                        log.Debug($"[{nameof(GameRoom)}.{nameof(YieldTicksAsync)}] nextFrameSpan: {nextFrameSpan.TotalMicroseconds}");
                        await Task.Delay(nextFrameSpan);
                    }
                    var isTryWriteSucc = collectMqTx.TryWrite(new FrameTick(tickOffset));
                    if (!isTryWriteSucc)
                    {
                        var m = "collectMqTx.TryWrite failed";
                        log.Error(m);
                        throw new Exception(m);
                    }
                    log.Debug($"[{nameof(GameRoom)}.{nameof(YieldTicksAsync)}] enqueued tick: {tickOffset}");
                    tickOffset += 1;
                }
                catch (Exception e)
                {
                    log.Error($"{e}");
                }
            }
        }

        private async Task YieldCountDownAsync(
            uint countdownBegin,
            uint ticksPerSecond,
            CancellationToken token = default)
        {
            var log = Logger.Shared;
            try
            {
                byte countUp = 0;
                var collectMqRx = this.collectMq_.Reader;
                var dispatchMqTx = this.dispatchMq_.Writer;
                while (true)
                {
                    var maybeMsg = await collectMqRx.ReadAsync(token);
                    if (maybeMsg is not IDemoPlayMessage msg)
                        throw new Exception("collectMqRx.ReadAsync failed");
                    if (msg is not FrameTick frameTickMsg)
                        continue;

                    var tick = frameTickMsg.Tick;
                    if (tick % ticksPerSecond == 0)
                    {
                        var countdown = (byte)(countdownBegin - countUp);
                        var message = new GameStartCountdown(countdown);
                        var isTryWriteSucc = dispatchMqTx.TryWrite(message);
                        if (!isTryWriteSucc)
                            throw new Exception("dispatchMqTx.TryWrite failed");

                        log.Debug($"[{nameof(GameRoom)}.{nameof(YieldCountDownAsync)}] count down: {countdown}, time: {DateTimeOffset.Now.ToString("O")}");
                        countUp++;
                    }
                    else
                        log.Debug($"[{nameof(GameRoom)}.{nameof(YieldCountDownAsync)}] tick: {tick}, time: {DateTimeOffset.Now.ToString("O")}");
                    if (countUp == countdownBegin)
                        break;
                }
            }
            catch (Exception e)
            {
                log.Debug($"{e}");
                throw;
            }
        }

        private async static Task LoopCollectPlayerMessagesAsync(
            Player player,
            Cysharp.Threading.Tasks.ChannelWriter<IDemoPlayMessage> collectMq,
            CancellationToken token = default)
        {
            var log = Logger.Shared;
            try
            {
                log.Debug($"[{nameof(GameRoom)}.{nameof(LoopCollectPlayerMessagesAsync)}] start loop for player#{player.Id} from ({player.Socket.RemoteEndPoint})");

                while (!token.IsCancellationRequested)
                {
                    var maybeMsg = await player.PlayerPushed.DequeueAsync(token);
                    if (!maybeMsg.TryOk(out var msg, out var pullErr))
                        throw pullErr.AsException();
                    if (msg is not PlayerFrameAct frameActMsg)
                    {
                        log.Debug($"[{nameof(GameRoom)}.{nameof(LoopCollectPlayerMessagesAsync)}] player#{player.Id} unexpected msg type: {msg.GetType().FullName} from player({player.Id})");
                        continue;
                    }

                    log.Debug($"[{nameof(GameRoom)}.{nameof(LoopCollectPlayerMessagesAsync)}] player#{player.Id}, from PlayerPushed dequeue frameAct: frame({frameActMsg.Act.Frame})");

                    var isTryWriteSucc = collectMq.TryWrite(frameActMsg);
                    if (!isTryWriteSucc)
                        throw new Exception("collectMq.TryWrite failed");

                    log.Debug($"[{nameof(GameRoom)}.{nameof(LoopCollectPlayerMessagesAsync)}] player#{player.Id}, into collectMq    enqueue frameAct: frame({frameActMsg.Act.Frame})");
                }
            }
            catch (Exception e)
            {
                log.Debug($"{e}");
                throw;
            }
        }

        private async Task YieldAggrFrameMessageAsync(
            Cysharp.Threading.Tasks.ChannelReader<IDemoPlayMessage> collectMq,
            Cysharp.Threading.Tasks.ChannelWriter<IDemoPlayMessage> dispatchMq,
            uint fps,
            uint roomCapacity,
            CancellationToken token = default)
        {
            var log = Logger.Shared;
            uint tickOffset = GameRoom.COUNTDOWN_TOTAL_SECONDS * fps;
            uint currFrame = 0;
            var collectedMsgDict = new SortedDictionary<PlayerId, List<PlayerFrameAct>>();
            while (true)
            {
                try
                {
                    var maybeSrc = await collectMq.ReadAsync(token);
                    if (maybeSrc is not IDemoPlayMessage srcMsg)
                        throw new Exception("Read collectMq failed");

                    if (srcMsg is FrameTick frameTickMsg)
                    {
                        currFrame = frameTickMsg.Tick - tickOffset;

                        if (currFrame % fps == 0)
                            log.Debug($"[{nameof(GameRoom)}.{nameof(YieldAggrFrameMessageAsync)}] currFrame({currFrame})");

                        var inputDict = new SortedDictionary<PlayerId, FrameAct[]>();
                        foreach (var kv in collectedMsgDict)
                            inputDict.Add(kv.Key, kv.Value.Select(p => p.Act).ToArray());

                        if (inputDict.Count == 0)
                        {
                            // 如果本帧没有收集到任何数据，直接不发送任何数据
                            log.Debug($"[{nameof(GameRoom)}.{nameof(YieldAggrFrameMessageAsync)}] currFrame({currFrame}) no msg collected.");
                            continue;
                        }
                        var aggrMsg = new OneFrameInputsMessage(inputDict);
                        var isTryWritesucc = dispatchMq.TryWrite(aggrMsg);
                        if (!isTryWritesucc)
                            log.Debug($"[{nameof(GameRoom)}.{nameof(YieldAggrFrameMessageAsync)}] currFrame({currFrame}) try write to dispatMq failed");
                        else
                            log.Debug($"[{nameof(GameRoom)}.{nameof(YieldAggrFrameMessageAsync)}] currFrame({currFrame}) aggrMsg enqueued({collectedMsgDict}).");

                        collectedMsgDict.Clear();
                    }
                    else if (srcMsg is PlayerFrameAct playerAction)
                    {
                        log.Debug($"[{nameof(GameRoom)}.{nameof(YieldAggrFrameMessageAsync)}] currFrame({currFrame}) act from player({playerAction.Id})");

                        if (collectedMsgDict.TryGetValue(playerAction.Id, out var collectedList))
                            collectedList.Add(playerAction);
                        else
                            collectedMsgDict.Add(playerAction.Id, new List<PlayerFrameAct> { playerAction });
                    }
                    else
                        log.Debug($"[{nameof(GameRoom)}.{nameof(YieldAggrFrameMessageAsync)}] currFrame({currFrame}) unexpected msg type({srcMsg.GetType().FullName})");
                }
                catch (Exception e)
                {
                    log.Error($"{e}");
                }
            }
        }

        private async Task DispatchMessageAsync(CancellationToken token = default)
        {
            var log = Logger.Shared;
            try
            {
                var dispatchMq = this.dispatchMq_.Reader;
                while (true)
                {
                    var maybeMsg = await dispatchMq.ReadAsync(token);
                    if (maybeMsg is not IDemoPlayMessage msg)
                        throw new Exception("read dispatchMq failed");

                    log.Debug($"[{nameof(GameRoom)}.{nameof(DispatchMessageAsync)}] from dispatchMq({msg.GetType().FullName})");

                    foreach (var player in this.players_)
                    {
                        var serverPushMq = player.ServerPushQueue;

                        log.Debug($"[{nameof(GameRoom)}.{nameof(DispatchMessageAsync)}] player(id:{player.Id}), from dispatchMq({msg.GetType().FullName}), into serverPushQueue({serverPushMq.GetHashCodeStrX8()})");

                        var isTryWriteSucc = serverPushMq.Writer.TryWrite(msg);
                        if (!isTryWriteSucc)
                            log.Debug($"[{nameof(GameRoom)}.{nameof(DispatchMessageAsync)}] serverPushMq.TryWrite failed");
                        else
                            log.Debug($"[{nameof(GameRoom)}.{nameof(DispatchMessageAsync)}] sent msg({msg.GetType().FullName}) to player(id: {player.Id}, ep: {player.Socket.RemoteEndPoint})");
                    }
                }
            }
            catch (Exception e)
            {
                log.Debug($"{e}");
                throw;
            }
        }
    }

}