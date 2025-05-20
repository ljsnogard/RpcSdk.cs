namespace LiquidRainbow.Mar07
{
    using System;
    using System.Collections.Generic;

    using PlayerId = System.UInt32;

    /// <summary>
    /// Demo 阶段游戏房间内的消息抽象
    /// </summary>
    public partial interface IDemoPlayMessage
    {}

    public static class Locations
    {
        public static Uri GetPlayerPushLocation(uint roomCapacity, uint playerId)
            => new($"rpc://lr.demo/g{roomCapacity}/p{playerId}.push");

        public static Uri GetGameRoomPullLocatioin(uint roomCapacity, uint playerId)
            => new($"rpc://lr.demo/g{roomCapacity}/p{playerId}.pull");
    }

    public partial struct GameRoomListing : IDemoPlayMessage
    {
        public Dictionary<string, GameRoomListingInfo> Listing { get; set; }

        public GameRoomListing(Dictionary<string, GameRoomListingInfo> listing)
            => this.Listing = listing;
    }

    public partial struct GameRoomListingInfo
    {
        public string RoomName { get; set; }

        /// <summary>
        /// 游戏房间玩家容量
        /// </summary>
        public uint Capacity { get; set; }

        /// <summary>
        /// 已进入玩家数量
        /// </summary>
        public uint Count { get; set; }

        /// <summary>
        /// 游戏房间信息最近更新时间
        /// </summary>
        public DateTimeOffset UTime { get; set; }
    }

    /// <summary>
    /// 服务端向玩家推送的进入房间的消息
    /// </summary>
    public partial struct GameRoomEnter : IDemoPlayMessage
    {
        /// <summary>
        /// 当前游戏房间内的玩家 ID 及显示名
        /// </summary>
        public Dictionary<PlayerId, string> Players { get; set; }

        /// <summary>
        /// 新进入的玩家的 ID
        /// </summary>
        public PlayerId NewPlayerId { get; set; }

        /// <summary>
        /// 玩家所进入的游戏房间的 ID。
        /// 当玩家是房间创建者时，用于告诉玩家房间创建成功。
        /// </summary>
        public string RoomId { get; set; }

        /// <summary>
        /// 游戏房间的最大玩家数量
        /// </summary>
        public uint RoomCapacity { get; set; }

        public GameRoomEnter
            ( Dictionary<PlayerId, string> players
            , PlayerId newPlayerId
            , string roomId
            , uint roomCapacity)
        {
            this.Players = players;
            this.NewPlayerId = newPlayerId;
            this.RoomId = roomId;
            this.RoomCapacity = roomCapacity;
        }
    }

    /// <summary>
    /// 客户端向服务端推送的进入房间的消息
    /// </summary>
    public partial struct PlayerEnter : IDemoPlayMessage
    {
        public PlayerId PlayerId { get; set; }

        /// <summary>
        /// 当玩家选择创建游戏房间时，此字段留空
        /// </summary>
        public string RoomId { get; set; }

        /// <summary>
        /// 游戏房间所能容纳最大玩家数量
        /// </summary>
        public uint GameRoomCapacity { get; set; }

        /// <summary>
        /// 玩家在对局中显示的昵称
        /// </summary>
        public string PlayerName { get; set; }

        /// <summary>
        /// 游戏房间的由创建者所定义的显示名
        /// </summary>
        public string RoomName { get; set; }

        public PlayerEnter
            ( PlayerId playerId
            , string roomId
            , uint gameRoomCapacity
            , string playerName
            , string roomName)
        {
            this.PlayerId = playerId;
            this.RoomId = roomId;
            this.PlayerName = playerName;
            this.RoomName = roomName;
            this.GameRoomCapacity = gameRoomCapacity;
        }
    }

    public partial struct GameStartCountdown : IDemoPlayMessage
    {
        public byte Signal { get; set; }

        public GameStartCountdown(byte signal)
            => this.Signal = signal;
    }

    public readonly struct Coordinate2D<T>
    {
        public readonly T X;
        public readonly T Y;

        public Coordinate2D(T x, T y)
        {
            this.X = x;
            this.Y = y;
        }
    }

    public readonly struct Coordinate3D<T>
    {
        public readonly T X;
        public readonly T Y;
        public readonly T Z;

        public Coordinate3D(T x, T y, T z)
        {
            this.X = x;
            this.Y = y;
            this.Z = z;
        }
    }

    public readonly struct LRInputMessage
    {
        public readonly Coordinate2D<long> Move;
        public readonly Coordinate2D<long> Shot;
        public readonly int Opt;

        public LRInputMessage(
            Coordinate2D<long> move,
            Coordinate2D<long> shot,
            int opt)
        {
            this.Move = move;
            this.Shot = shot;
            this.Opt = opt;
        }

        public static LRInputMessage Create(
            long moveX,
            long moveY,
            long shotX,
            long shotY,
            int opt)
        {
            return new(
                move: new Coordinate2D<long>(moveX, moveY),
                shot: new Coordinate2D<long>(shotX, shotY),
                opt : opt
            );
        }
    }

    public readonly struct LRBulletMessage
    {
        /// <summary>
        /// 子弹所打中的喷涂物
        /// 场景内的可喷涂物有唯一id，而且各端统一。
        /// </summary>
        public readonly uint PaintId;
        /// <summary>
        /// 子弹碰撞点的坐标。
        /// </summary>
        public readonly Coordinate3D<float> Paint;
        public readonly float Radius;

        /// <summary>
        /// 我自己的Hp值，同步给其他人，这个数据放这里是因为只有子弹才会影响Hp的值。
        /// </summary>
        public readonly uint PlayerHp;
        /// <summary>
        /// 子弹是第几帧生成的。
        /// </summary>
        public readonly uint Frame;
        /// <summary>
        /// 子弹打到谁了，那个谁收到消息后，匹配TargetId和自己相同就更新自己的PlayerHp，再把自己的Hp同步出去。
        /// </summary>
        public readonly uint TargetId;

        public LRBulletMessage(
            uint paintId,
            Coordinate3D<float> paint,
            float radius,
            uint playerHp,
            uint frame,
            uint targetId)
        {
            this.PaintId = paintId;
            this.Paint = paint;
            this.Radius = radius;
            this.PlayerHp = playerHp;
            this.Frame = frame;
            this.TargetId = targetId;
        }

        public static LRBulletMessage Create(
            uint paintId,
            float paintX,
            float paintY,
            float paintZ,
            float radius,
            uint playerHp,
            uint frame,
            uint targetId)
        {
            return new(
                paintId : paintId,
                paint   : new Coordinate3D<float>(paintX, paintY, paintZ),
                radius  : radius,
                playerHp: playerHp,
                frame   : frame,
                targetId: targetId
            );
        }
    }

    /// <summary>
    /// 前端发送的帧同步消息。
    /// </summary>
    public partial struct PlayerFrameAct : IDemoPlayMessage
    {
        public PlayerId Id { get; set; }

        public FrameAct Act { get; set; }

        public PlayerFrameAct(PlayerId id, FrameAct act)
        {
            this.Id = id;
            this.Act = act;
        }
    }

    public partial struct FrameAct
    {
        public uint Frame { get; set; }

        public LRInputMessage Input { get; set; }

        public LRBulletMessage Bullet { get; set; }

        public FrameAct(uint frame, LRInputMessage input, LRBulletMessage bullet)
        {
            this.Frame = frame;
            this.Input = input;
            this.Bullet = bullet;
        }
    }

    /// <summary>
    /// 后端推送的帧同步消息。
    /// </summary>
    public partial struct OneFrameInputsMessage : IDemoPlayMessage
    {
        public SortedDictionary<PlayerId, FrameAct[]> Acts { get; set; }

        public OneFrameInputsMessage(SortedDictionary<PlayerId, FrameAct[]> acts)
            => this.Acts = acts;
    }
}