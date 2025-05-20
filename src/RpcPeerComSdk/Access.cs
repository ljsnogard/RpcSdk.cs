namespace RpcPeerComSdk
{
    using System;

    public readonly struct AccessMethod
    {
        public readonly byte Code;

        internal AccessMethod(byte code)
            => this.Code = code;

        /// <summary>
        /// 查询某个资源的有效访问方法，此方法对所有资源有效，可以被缓存
        /// </summary>
        public static readonly AccessMethod Head = new(0b_0000_0000);

        /// <summary>
        /// 查看指定 Uri 上的资源，其数据格式通过头部指定，内容通过响应荷载发送，此类结果通常可以被缓存。
        /// </summary>
        public static readonly AccessMethod View = new(0b_0000_0001);

        /// <summary>
        /// 上传资源到指定的 uri，其数据格式通过头部制定，内容通过请求体发送。
        /// </summary>
        public static readonly AccessMethod Post = new(0b_0000_0010);

        /// <summary>
        /// 删除资源
        /// </summary>
        public static readonly AccessMethod Drop = new(0b_0000_0100);

        /// <summary>
        /// 拉取数据（仅对数据流类资源有效）
        /// </summary>
        public static readonly AccessMethod Pull = new(0b_0001_0000);

        /// <summary>
        /// 推送数据（仅对数据流类资源有效）
        /// </summary>
        public static readonly AccessMethod Push = new(0b_0010_0000);

        /// <summary>
        /// 调用 API，该API 由 Uri 指定，参数格式由请求头指示，参数内容在请求体
        /// </summary>
        public static readonly AccessMethod Call = new(0b_0100_0000);

        public static AccessMethod operator |(AccessMethod a, AccessMethod b)
            => new AccessMethod((byte)(a.Code | b.Code));

        public bool Allows(AccessMethod method)
            => (this.Code & method.Code) == method.Code;
    }

    /// <summary>
    /// 资源和服务的访问权限，分别为 System，Owner，Local，Remote 四种访问者身份配置不同的权限。
    /// System 权限即为对该资源或服务的所有有效操作方法的集合。
    /// </summary>
    public readonly struct AccessPermissions
    {
        public readonly UInt32 Code;

        private AccessPermissions(UInt32 code)
            => this.Code = code;

        public static byte GetSystem(UInt32 code)
            => (byte)(code >> 24);

        public static void SetSystem(ref UInt32 code, byte part)
            => code |= ((UInt32)part) << 24;

        public static byte GetOwner(UInt32 code)
            => (byte)(code >> 16);

        public static void SetOwner(ref UInt32 code, byte part)
            => code |= ((UInt32)part) << 16;

        public static byte GetLocal(UInt32 code)
            => (byte)(code >> 8);

        public static void SetLocal(ref UInt32 code, byte part)
            => code |= ((UInt32)part) << 8;

        public static byte GetRemote(UInt32 code)
            => (byte)code;

        public static void SetRemote(ref UInt32 code, byte part)
            => code |= part;

        public static bool TryFromCode(UInt32 code, out AccessPermissions methodGroup)
        {
            var sys = new AccessMethod(AccessPermissions.GetSystem(code));
            var own = new AccessMethod(AccessPermissions.GetOwner(code));
            var loc = new AccessMethod(AccessPermissions.GetLocal(code));
            var rem = new AccessMethod(AccessPermissions.GetRemote(code));
            if (sys.Allows(own) && sys.Allows(loc) && sys.Allows(rem))
            {
                methodGroup = new AccessPermissions(code);
                return true;
            }
            methodGroup = default;
            return false;
        }

        /// <summary>
        /// 系统对资源的访问权限，亦即资源的自身的访问方法
        /// </summary>
        public AccessMethod System
            => new AccessMethod(AccessPermissions.GetSystem(this.Code));

        /// <summary>
        /// 资源所有者的访问权限
        /// </summary>
        public AccessMethod Owner
            => new AccessMethod(AccessPermissions.GetOwner(this.Code));

        /// <summary>
        /// 同 Host 内的访问者对资源的访问权限
        /// </summary>
        public AccessMethod Local
            => new AccessMethod(AccessPermissions.GetLocal(this.Code));

        /// <summary>
        /// Host 以外的访问者对资源的访问权限
        /// </summary>
        public AccessMethod Remote
            => new AccessMethod(AccessPermissions.GetRemote(this.Code));
    }
}
