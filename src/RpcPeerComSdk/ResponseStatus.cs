namespace RpcPeerComSdk
{
    public readonly partial struct ResponseStatus
    {
        /// <summary>
        /// 成功响应 View Head Call 等一次性往返请求
        /// </summary>
        public static readonly ResponseStatus Ok = new(200);

        /// <summary>
        /// 成功响应 Push Pull 等请求
        /// </summary>
        public static readonly ResponseStatus Ready = new(201);

        /// <summary>
        /// 即将停止响应 Push Pull 
        /// </summary>
        public static readonly ResponseStatus Final = new(202);

        public static readonly ResponseStatus BadRequest = new(400);

        public static readonly ResponseStatus MethodDenied = new(401);

        public static readonly ResponseStatus PermissionDenied = new(402);

        public static readonly ResponseStatus NotFound = new(404);

        /// <summary>
        /// 一般用于网关主动向客户端发送错误通知，并关闭反向代理的服务端通信。
        /// </summary>
        public static readonly ResponseStatus ServerError = new(500);

        public readonly ushort Code;

        private ResponseStatus(ushort code)
            => this.Code = code;
    }
}