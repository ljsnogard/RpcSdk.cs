namespace RpcClientSdk
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using BufferKit;

    using RpcMuxSdk;
    using RpcPeerComSdk;

    public interface IClient
    {
        /// <summary>
        /// 发送请求
        /// </summary>
        /// <param name="method">访问资源的方法</param>
        /// <param name="location">所请求访问资源的位置 Uri 表示</param>
        /// <param name="headers">附加零个或多个请求头数据</param>
        /// <param name="body">请求体自身的数据</param>
        /// <param name="token"></param>
        /// <returns></returns>
        public UniTask<Result<IResponse<TResult>, IClientError>> RequestAsync<TReqeust, TResult>(
            AccessMethod accessMethod,
            Uri location,
            IAsyncEnumerable<Header> headers,
            TReqeust body,
            CancellationToken token = default
        );
    }

    /// <summary>
    /// 从服务器中获取指定地址上的静态资源的某一个格式的视图，该格式由 headers 指定
    /// </summary>
    public interface IViewerClient : IClient
    {
        public UniTask<Result<IBuffRx<byte>, IClientError>> ViewAsync(
            Uri location,
            IAsyncEnumerable<Header> headers,
            CancellationToken token = default
        );
    }

    /// <summary>
    /// 从服务器中获取指定地址上的静态资源并反序列化为指定类型的对象
    /// </summary>
    /// <typeparam name="TItem"></typeparam> <summary>
    public interface IViewerClient<TItem> : IViewerClient
    {
        public UniTask<Result<TItem, IClientError>> ViewAsync(
            Uri location,
            CancellationToken token = default
        );
    }

    /// <summary>
    /// 调用由指定地址上的 API
    /// </summary>
    /// <typeparam name="TArg"></typeparam>
    /// <typeparam name="TRes"></typeparam> <summary>
    /// 
    /// </summary>
    /// <typeparam name="TArg"></typeparam>
    /// <typeparam name="TRes"></typeparam>
    public interface ICallerClient<TArg, TRes> : IClient
    {
        public UniTask<Result<TRes, IClientError>> CallAsync(
            Uri location,
            IAsyncEnumerable<Header> headers,
            TArg arguments,
            CancellationToken token = default
        );
    }

    public interface IPusherClient : IClient
    {
        public UniTask<Result<IPushAgent, IClientError>> PushAsync(
            Uri location,
            IAsyncEnumerable<Header> headers,
            CancellationToken token = default
        );
    }

    public interface IPusherClient<TItem> : IPusherClient
    {
        public UniTask<Result<IPushAgent<TItem>, IClientError>> PushAsync<THeaders>(
            Uri location,
            THeaders headers,
            CancellationToken token = default)
        where THeaders : IAsyncEnumerable<Header>;
    }

    public interface IPullerClient : IClient
    {
        public UniTask<Result<IPullAgent, IClientError>> PullAsync(
            Uri location,
            IAsyncEnumerable<Header> headers,
            CancellationToken token = default
        );
    }

    public interface IPullerClient<TItem> : IClient
    {
        public UniTask<Result<IPullAgent<TItem>, IClientError>> PullAsync<THeaders>(
            Uri location,
            THeaders headers,
            CancellationToken token = default)
        where THeaders : IAsyncEnumerable<Header>;
    }

    public static class ClientExtensions
    {
        public static async UniTask<Result<TItem, IClientError>> ViewAsync<TItem>(
            this IClient client,
            Uri location,
            CancellationToken token = default)
        {
            if (client is IViewerClient<TItem> viewer)
                return await viewer.ViewAsync(location, token);

            var response = await client.RequestAsync<EmptyRequestBody, TItem>(
                accessMethod: AccessMethod.View,
                location: location,
                headers: NoHeaders.Instance,
                body: new EmptyRequestBody(),
                token: token
            );
            if (response is not IResponse<TItem> resp)
                throw new Exception("Unexpected result");

            var readRes = await resp.ReadBodyAsync(token);
            if (!readRes.TryOk(out var item , out var respErr))
                throw respErr.AsException();
            return Result.Ok(item);
        }

        #region Invoke

        public static UniTask<Result<TRes, IClientError>> CallAsync<TArg, TRes>(
            this IClient client,
            Uri location,
            TArg arguments,
            CancellationToken token = default)
        {
            return client.CallAsync<TArg, TRes>(
                location: location,
                headers: NoHeaders.Instance,
                arguments: arguments,
                token: token
            );
        }

        public static async UniTask<Result<TRes, IClientError>> CallAsync<TArg, TRes>(
            this IClient client,
            Uri location,
            IAsyncEnumerable<Header> headers,
            TArg arguments,
            CancellationToken token = default)
        {
            if (client is ICallerClient<TArg, TRes> caller)
                return await caller.CallAsync(location, headers, arguments, token);

            var reqRes = await client.RequestAsync<TArg, TRes>(
                accessMethod: AccessMethod.Call,
                location: location,
                headers: headers,
                body: arguments,
                token: token
            );
            if (!reqRes.TryOk(out var response, out var clientError))
                throw clientError.AsException();
            throw new NotImplementedException();
        }

        #endregion

        #region Pull

        /// <summary>
        /// 获取一个拉流助理来准备以 Pull 的方法来访问资源
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        public static async UniTask<Result<IPullAgent<TItem>, IClientError>> PullAsync<TItem>(
            this IClient client,
            Uri location,
            IAsyncEnumerable<Header> headers,
            CancellationToken token = default)
        {
            if (client is IPullerClient<TItem> puller)
                return await puller.PullAsync(location, headers);
            throw new NotImplementedException();
        }

        #endregion

        #region Push

        /// <summary>
        /// 获取一个推流助理
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        public static async UniTask<Result<IPushAgent<TItem>, IClientError>> PushAsync<TItem>(
            this IClient client,
            Uri location,
            IAsyncEnumerable<Header> headers,
            CancellationToken token = default)
        {
            if (client is IPusherClient<TItem> pusher)
                return await pusher.PushAsync(location, headers, token);
            throw new NotImplementedException();
        }

        #endregion
    }
}
