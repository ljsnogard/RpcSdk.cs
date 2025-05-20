namespace RpcPeerComSdk
{
    public readonly partial struct ResponseStatus
    {
        public static readonly ResponseStatus Ok = new(200);

        public static readonly ResponseStatus BadRequest = new(400);

        public static readonly ResponseStatus MethodDenied = new(401);

        public static readonly ResponseStatus PermissionDenied = new(402);

        public static readonly ResponseStatus NotFound = new(404);

        public static readonly ResponseStatus ServerError = new(500);

        public readonly ushort Code;

        private ResponseStatus(ushort code)
            => this.Code = code;
    }
}