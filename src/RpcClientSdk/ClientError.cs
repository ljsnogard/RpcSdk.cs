namespace RpcClientSdk
{
    using System;

    public interface IClientError
    {
        public Exception AsException();
    }
}