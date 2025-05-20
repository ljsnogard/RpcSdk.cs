namespace RpcServerSdk
{
    using System;

    using Cysharp.Threading.Tasks;

    using OneOf;

    using RpcPeerComSdk;

    public readonly struct ConfigureServicesError
    {
        public readonly byte Code;

        private ConfigureServicesError(byte code)
            => this.Code = code;

        public static readonly ConfigureServicesError Conflict = new(1);
    }

    public interface IRequestError
    {}

    /// <summary>
    /// Handler that accepts request with type <c>TRequest</c> and returns response with type <c>TResponse</c>
    /// </summary>
    /// <typeparam name="TRequest">The request data type</typeparam>
    /// <typeparam name="TResponse">The response data type</typeparam>
    /// <param name="uri">The Uri this request claims to access resource or service.</param>
    /// <param name="accessMethod">The method this request tries with to get access to the resource.</param>
    /// <param name="headers"></param>
    /// <param name="request">The request argument data.</param>
    /// <param name="token">Cancellation token passed by the host.</param>
    /// <returns></returns>
    public delegate UniTask<OneOf<TResponse, IRequestError>> FnHandleRequest<TRequest, TResponse>(
        Uri uri,
        AccessMethod accessMethod,
        IAsyncEnumerable<Header> headers,
        TRequest request,
        CancellationToken token
    );

    public interface IConfigureServices
    {
        /// <summary>
        /// 添加一个服务或者资源访问接口。
        /// </summary>
        /// <typeparam name="TRequest">要服务的请求类型</typeparam>
        /// <typeparam name="TResponse">服务（成功）结果的类型</typeparam>
        /// <param name="uriTemplate">服务或资源的相对路径。例如：<code>"resource/{id}"</code></param>
        /// <param name="accessMethod">设置资源或服务的访问方法</param>
        /// <param name="handler">响应请求要调用的方法</param>
        /// <returns></returns>
        /// <remarks>
        /// 相同的 uriPattern 可以对应具有不同 AccessMethod 配置的 handler，这些 handler 响应的结果类型之间没有必然联系。
        /// 譬如，以 View 和 Post 方法访问同一个 uri 显然可以设计为不同的返回结果。
        /// </remarks>
        public OneOf<IConfigureServices, ConfigureServicesError> AddHandler<TRequest, TResponse>(
            string uriTemplate,
            AccessMethod accessMethod,
            FnHandleRequest<TRequest, TResponse> handler
        );

        public OneOf<IConfigureServices, ConfigureServicesError> SetPermissions(
            string uriTemplate,
            AccessPermissions permissions
        );
    }
}
