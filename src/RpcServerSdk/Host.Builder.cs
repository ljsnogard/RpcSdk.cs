namespace RpcServerSdk
{
    using System;
    using System.Net;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    public interface IHostBuilder
    {
        public delegate UniTask FnConfigureServicesAsync(IConfigureServices configureServices, CancellationToken token);

        public delegate UniTask FnConfigureLighthousesAsync(IConfigureLighthouses configureLighthouse, CancellationToken token);

        public IHostBuilder ListeningAt<TEndp>(Func<TEndp> configureListeningEndpoint)
            where TEndp: EndPoint;

        public IHostBuilder IdentifiedBy(Func<HostEntry> generateId);

        /// <summary>
        /// 配置 Lighthouse 节点信息，即节点发现、节点存活检测、配置下发等各种集群自维护服务
        /// </summary>
        /// <param name="configureLighthouses"></param>
        /// <returns></returns>
        public IHostBuilder ConfigureLighthouses(FnConfigureLighthousesAsync configureLighthouses);

        /// <summary>
        /// 配置 Host 的资源和服务接口，这些资源和服务可以相互访问
        /// </summary>
        /// <param name="configureServices"></param>
        /// <returns></returns>
        public IHostBuilder ConfigureServices(FnConfigureServicesAsync configureServices);
    }
}
