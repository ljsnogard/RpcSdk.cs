namespace LiquidRainbow.V0.IAM
{
    using System;

    public static class AuthMethods
    {
        /// <summary>
        /// 测试阶段可以用账号密码体系，估计上线产品不会用
        /// </summary>
        public static readonly string ID_PASSWORD = nameof(ID_PASSWORD);

        /// <summary>
        /// 自生成证书
        /// </summary>
        public static readonly string PK_ED25519 = nameof(PK_ED25519);

        /// <summary>
        /// 微信 OAUTH 授权登陆
        /// </summary>
        public static readonly string OAUTH_WEIXIN = nameof(OAUTH_WEIXIN);

        /// <summary>
        /// QQ OAUTH 授权登陆
        /// </summary>
        public static readonly string OAUTH_QQ = nameof(OAUTH_QQ);

        /// <summary>
        /// 抖音（国内）OAUTH 授权登陆
        /// </summary>
        public static readonly string OAUTH_DOUYIN = nameof(OAUTH_DOUYIN);

        /// <summary>
        /// 推特（x.com）OAUTH 授权登陆
        /// </summary>
        public static readonly string OAUTH_TWITTER = nameof(OAUTH_TWITTER);

        /// <summary>
        /// 脸书（facebook.com）OAUTH 授权登陆
        /// </summary>
        public static readonly string OAUTH_FACEBOOK = nameof(OAUTH_FACEBOOK);
    }

    public readonly struct LoginRequest
    {
        /// <summary>
        /// 要采用的登陆验证方法
        /// </summary>
        public readonly string Method;

        /// <summary>
        /// 用户昵称或者用户 ID，不同的 AuthMethod 的值有不同的意义
        /// </summary>
        public readonly string User;

        public readonly DateTimeOffset Expiration;

        /// <summary>
        /// 验证用户身份的凭据，根据 AuthMethod 的值可以是加盐密码也可以是数字签名
        /// </summary>
        public readonly ReadOnlyMemory<byte> Credential;

        public LoginRequest(string method, string user, DateTimeOffset expiration, ReadOnlyMemory<byte> credential)
        {
            this.Method = method;
            this.User = user;
            this.Expiration = expiration;
            this.Credential = credential;
        }

        /// <summary>
        /// 使用用户名和密码登录
        /// </summary>
        /// <param name="user">用户名</param>
        /// <param name="password">明文密码</param>
        /// <param name="life">登陆有效时长</param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public static LoginRequest IdPassword(string user, string password, TimeSpan life)
            => throw new NotImplementedException();

        /// <summary>
        /// 使用公钥加密算法登录，需要输入用户证书
        /// </summary>
        /// <param name="user">用户名</param>
        /// <param name="life">登陆有效时长</param>
        /// <param name="privateKey">用于签名的用户私钥</param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public static LoginRequest Ed25519(string user, TimeSpan life, object signKey)
            => throw new NotImplementedException();
    }

    public readonly struct LoginSuccResponse
    {
        /// <summary>
        /// 用户登陆凭据，可用快速重连等
        /// </summary>
        public readonly ReadOnlyMemory<byte> Token;

        public readonly string Method;

        public readonly DateTimeOffset Expiration;
    }

    /// <summary>
    /// 快速登陆，自动登陆
    /// </summary>
    public readonly struct FastLogin
    {
        public readonly string User;

        public readonly string Method;

        public readonly ReadOnlyMemory<byte> Token;
    }
}
