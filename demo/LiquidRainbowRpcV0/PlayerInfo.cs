namespace LiquidRainbow.V0.PlayerInfo
{
    using System;
    using System.Collections.Generic;

    public static class PlayerFieldNames
    {
        /// <summary>
        /// 玩家 ID
        /// </summary>
        public static readonly string Pid = nameof(Pid);

        /// <summary>
        /// 昵称
        /// </summary>
        public static readonly string Name = nameof(Name);

        /// <summary>
        /// 头像
        /// </summary>
        public static readonly string Avatar = nameof(Avatar);
    }

    /// <summary>
    /// 用于查询一个或多个玩家相关信息的请求，
    /// </summary>
    public readonly struct QueryPlayerInfoRequest
    {
        public readonly string UserId;

        public readonly ReadOnlyMemory<string> Fields;

        public QueryPlayerInfoRequest(string userId, ReadOnlyMemory<string> fields)
        {
            this.UserId = userId;
            this.Fields = fields;
        }
    }

    public readonly struct QueryPlayerInfoResult
    {
        public readonly string UserId;

        public readonly string DisplayName;

        public readonly IReadOnlyDictionary<string, string> Fields;
    }
}