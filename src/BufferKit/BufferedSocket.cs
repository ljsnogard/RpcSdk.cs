// ReSharper disable InconsistentNaming
namespace BufferKit
{
    using System;
    using System.Net;
    using System.Net.Sockets;

    using System.Threading;
    using System.Threading.Tasks;

    using Cysharp.Threading.Tasks;

    using LoggingSdk;

    /// <summary>
    /// 内部带有读写缓冲的 socket
    /// </summary>
    public readonly struct BufferedSocketError : IIoError
    {
        public readonly string Message;

        public BufferedSocketError(string message)
            => this.Message = message;

        public static implicit operator BufferedSocketError(RingBufferError buffErr)
            => new BufferedSocketError(buffErr.ToString());

        public static implicit operator BufferedSocketError(SocketError sockErr)
            => new BufferedSocketError(sockErr.ToString());

        public Exception AsException()
            => new Exception(this.Message);
    }

    /// <summary>
    /// 将一个 Socket (Tcp 或者 Unix Domain) 收取和发送的数据分别用 RingBuffer 缓存，以实现背压 (Back pressure) 和流量控制
    /// ，并支持其他复杂的读写操作。
    /// </summary>
    public sealed class BufferedSocket : IScspBuffer<byte>, IDisposable
    {
        private readonly Lazy<RingBuffer<byte>> rxBuffLazy_;

        private readonly Lazy<RingBuffer<byte>> txBuffLazy_;

        private readonly Lazy<Task> txloopLazy_;

        private readonly Lazy<Task> rxloopLazy_;

        private readonly CancellationTokenSource cts_;

        private readonly SemaphoreSlim txSema_;

        private readonly SemaphoreSlim rxSema_;

        private readonly SocketOutput tx_;

        private readonly SocketInput rx_;

        private bool isDisposed_;

        private BufferedSocket
            ( Func<RingBuffer<byte>> createRxBuff
            , Func<RingBuffer<byte>> createTxBuff
            , CancellationTokenSource tokenSource
            , Socket socket)
        {
            this.rxSema_ = new SemaphoreSlim(1, 1);
            this.txSema_ = new SemaphoreSlim(1, 1);
            this.rxBuffLazy_ = new Lazy<RingBuffer<byte>>(createRxBuff);
            this.txBuffLazy_ = new Lazy<RingBuffer<byte>>(createTxBuff);
            this.rxloopLazy_ = new Lazy<Task>(() => this.RxLoopAsync_(socket));
            this.txloopLazy_ = new Lazy<Task>(() => this.TxLoopAsync_(socket));
            this.cts_ = tokenSource;
            this.isDisposed_ = false;

            this.tx_ = new SocketOutput(socket);
            this.rx_ = new SocketInput(socket);
        }

        public static async UniTask<Socket> ConnectAsync(EndPoint server, CancellationToken token)
        {
            if (server is null)
                throw new ArgumentNullException(nameof(server));

            var protocolType = server switch
            {
                IPEndPoint => ProtocolType.Tcp,
                UnixDomainSocketEndPoint => ProtocolType.IP,
                _ => throw new NotSupportedException("Endpoint type not supported")
            };
            var tcs = new UniTaskCompletionSource<Socket>();
            using var args = new SocketAsyncEventArgs();
            args.RemoteEndPoint = server;
            args.Completed += (s, e) =>
            {
                if (e.SocketError == SocketError.Success)
                    tcs.TrySetResult((Socket)s);
                else
                    tcs.TrySetException(new SocketException((int)e.SocketError));
            };
            if (!Socket.ConnectAsync(SocketType.Stream, protocolType, args))
            {
                // Completed synchronously
                if (args.SocketError == SocketError.Success)
                    tcs.TrySetResult(args.ConnectSocket);
                else
                    throw new SocketException((int)args.SocketError);
            }
            return await tcs.Task.AttachExternalCancellation(token);
        }

        public static UniTask<BufferedSocket> ConnectAsync(
            EndPoint endpoint,
            NUsize bufferCapacity,
            CancellationToken token = default)
        {
            async UniTask<Socket> CreateSocketAsync_CapturingEndpoint_(CancellationToken tok)
                => await BufferedSocket.ConnectAsync(endpoint, tok);

            RingBuffer<byte> CreateRingBuffer_CapturingCapacity_()
                => new(bufferCapacity);

            return BufferedSocket.FromSocketAsync(
                createSocketAsync: CreateSocketAsync_CapturingEndpoint_,
                createTxBuffer: CreateRingBuffer_CapturingCapacity_,
                createRxBuffer: CreateRingBuffer_CapturingCapacity_,
                createTokenSource: () => new CancellationTokenSource(),
                token
            );
        }

        public static async UniTask<BufferedSocket> FromSocketAsync(
            Func<CancellationToken, UniTask<Socket>> createSocketAsync,
            Func<RingBuffer<byte>> createTxBuffer,
            Func<RingBuffer<byte>> createRxBuffer,
            Func<CancellationTokenSource> createTokenSource,
            CancellationToken token = default)
        {
            var isBufferCreated = false;
            Socket? socket = null;
            try
            {
                socket = await createSocketAsync(token);
                var tokSrc = createTokenSource();
                var socketBuffer = new BufferedSocket(
                    createRxBuff: createRxBuffer,
                    createTxBuff: createTxBuffer,
                    tokenSource: tokSrc,
                    socket: socket
                );
                isBufferCreated = true;
                return socketBuffer;
            }
            catch (Exception e)
            {
                Console.Out.WriteLine(e);
                throw;
            }
            finally
            {
                if (socket is Socket createdSocket && !isBufferCreated)
                    createdSocket.Dispose();
            }
        }

        public EndPoint LocalEndPoint
            => this.tx_.Socket.LocalEndPoint;

        public EndPoint RemoteEndPoint
            => this.tx_.Socket.RemoteEndPoint;

        public async UniTask<Result<ReaderBuffSegm<byte>, BufferedSocketError>> ReadAsync
            ( Demand demand
            , CancellationToken token = default)
        {
            try
            {
                await this.rxSema_.WaitAsync(token);

                var rxBuff = this.rxBuffLazy_.Value;
                if (this.IsRxClosed)
                    return Result.Err<BufferedSocketError>(SocketError.ConnectionReset);
                var maybeSegms = await rxBuff.ReadAsync(demand, token);
                return maybeSegms.MapErr((e) => (BufferedSocketError)e);
            }
            catch (Exception e)
            {
                Logger.Shared.Error($"{e}");
                throw;
            }
            finally
            {
                if (this.rxSema_.CurrentCount == 0)
                    this.rxSema_.Release();
            }
        }

        public async UniTask<Result<ReaderBuffSegm<byte>, BufferedSocketError>> PeekAsync
            ( NUsize offset
            , Demand demand
            , CancellationToken token = default)
        {
            try
            {
                await this.rxSema_.WaitAsync(token);

                var rxBuff = this.rxBuffLazy_.Value;
                if (this.IsRxClosed)
                    return Result.Err<BufferedSocketError>(SocketError.ConnectionReset);
                var maybeSegms = await rxBuff.PeekAsync(offset, demand, token);
                return maybeSegms.MapErr(e => (BufferedSocketError)e);
            }
            catch (Exception e)
            {
                Logger.Shared.Error($"{e}");
                throw;
            }
            finally
            {
                if (this.rxSema_.CurrentCount == 0)
                    this.rxSema_.Release();
            }
        }

        public async UniTask<Result<WriterBuffSegm<byte>, BufferedSocketError>> WriteAsync
            ( Demand demand
            , CancellationToken token = default)
        {
            try
            {
                await this.txSema_.WaitAsync(token);

                var txBuff = this.txBuffLazy_.Value;
                if (this.IsTxClosed)
                    return Result.Err<BufferedSocketError>(SocketError.ConnectionAborted);
                var maybeSegms = await txBuff.WriteAsync(demand, token);
                return maybeSegms.MapErr(e => (BufferedSocketError)e);
            }
            catch (Exception e)
            {
                Logger.Shared.Error($"{e}");
                throw;
            }
            finally
            {
                if (this.txSema_.CurrentCount == 0)
                    this.txSema_.Release();
            }
        }

        /// <summary>
        /// 检查 rxloop 是否已退出
        /// </summary>
        public bool IsRxClosed
            => this.rxloopLazy_.Value.IsCompleted;

        public NUsize RxCapacity
            => this.rxBuffLazy_.Value.Capacity;

        /// <summary>
        /// 检查 txloop 是否已退出
        /// </summary>
        public bool IsTxClosed
            => this.txloopLazy_.Value.IsCompleted;

        public NUsize TxCapacity
            => this.txBuffLazy_.Value.Capacity;

        #region Background loop

        /// <summary>
        /// 开启循环从 socket 中读取远端发送的数据并写到 rxBuff 中
        /// </summary>
        private async Task RxLoopAsync_(Socket socket)
        {
            var localEp = socket.LocalEndPoint;
            var remoteEp = socket.RemoteEndPoint;
            var token = this.cts_.Token;
            var rxBuff = this.rxBuffLazy_.Value;
            var inputProxy = this.rx_.GetCachedProxy();
            Logger.Shared.Debug($"[{nameof(BufferedSocket)}.{nameof(RxLoopAsync_)}](l: {localEp}, r: {remoteEp}) rxBuff({rxBuff.GetHashCodeStrX8()}) starting recv.");
            while (true)
            {
                try
                {
                    if (token.IsCancellationRequested)
                    {
                        Logger.Shared.Debug($"[{nameof(BufferedSocket)}.{nameof(RxLoopAsync_)}](l: {localEp}, r: {remoteEp}) exit because cancelled");
                        break;
                    }
                    // Logger.Shared.Debug($"[{nameof(BufferedSocket)}.{nameof(RxLoopAsync_)}](l: {localEp}, r: {remoteEp}) before rxBuff({rxBuff.GetHashCodeStrX8()}).WriteAsync({rxBuff.Capacity}), writerSize: ({rxBuff.WriterSize})");
                    var writeRes = await rxBuff.WriteAsync(Demand.Least, token);
                    if (!writeRes.TryOk(out var writerSegment, out var buffErr))
                        throw buffErr.AsException();

                    // Logger.Shared.Debug($"[{nameof(BufferedSocket)}.{nameof(RxLoopAsync_)}](l: {localEp}, r: {remoteEp}) after rxBuff({rxBuff.GetHashCodeStrX8()}).WriteAsync({rxBuff.Capacity}), writerSegments.Length({writerSegments.Length})");
                    using (writerSegment)
                    {
                        var readRes = await inputProxy.ReadAsync(writerSegment, token);
                        if (!readRes.TryOk(out var cpCount, out var readErr))
                            throw readErr.AsException();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    Logger.Shared.Error($"[{nameof(BufferedSocket)}.{nameof(RxLoopAsync_)}](l: {localEp}, r: {remoteEp}) unexpected exception: {e}");
                    throw;
                }
            }
        }

        /// <summary>
        /// 开启循环从 txBuff 读取已缓存的数据并通过 socket 发送到远端
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private async Task TxLoopAsync_(Socket socket)
        {
            var localEp = socket.LocalEndPoint;
            var remoteEp = socket.RemoteEndPoint;
            var token = this.cts_.Token;
            var txBuff = this.txBuffLazy_.Value;
            var outputProxy =  this.tx_.GetCachedProxy();
            Logger.Shared.Debug($"[{nameof(BufferedSocket)}.{nameof(TxLoopAsync_)}](l: {localEp}, r: {remoteEp}) txBuff({txBuff.GetHashCode():X8}) starting send");
            while (true)
            {
                try
                {
                    if (token.IsCancellationRequested)
                    {
                        Logger.Shared.Debug($"[{nameof(BufferedSocket)}.{nameof(TxLoopAsync_)}](l: {localEp}, r: {remoteEp}) exit because cancelled");
                        break;
                    }
                    // Logger.Shared.Debug($"[{nameof(BufferedSocket)}.{nameof(TxLoopAsync_)}](l: {localEp}, r: {remoteEp}) before txBuff({txBuff.GetHashCode():X8}).ReadAsync({txBuff.Capacity}), readerSize: ({txBuff.ReaderSize})");
                    var readRes = await txBuff.ReadAsync(Demand.Least, token);
                    if (!readRes.TryOk(out var readerSegment, out var buffErr))
                        throw buffErr.AsException();
                    using (readerSegment)
                    {
                        var writeRes = await outputProxy.WriteAsync(readerSegment, token);
                        if (!writeRes.TryOk(out var cpCount, out var writeErr))
                            throw writeErr.AsException();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    Logger.Shared.Error($"[{nameof(BufferedSocket)}.{nameof(TxLoopAsync_)}](l: {localEp}, r: {remoteEp}) unexpected exception: {e}");
                    throw;
                }
            }
        }

        #endregion

        #region Implement IBuffer<byte>

        NUsize IBuffRx<byte>.Capacity
            => this.RxCapacity;

        NUsize IBuffTx<byte>.Capacity
            => this.TxCapacity;

        async UniTask<Result<ReaderBuffSegm<byte>, IIoError>> IBuffRx<byte>.ReadAsync
            ( Demand demand
            , CancellationToken token)
        {
            var x = await this.ReadAsync(demand, token);
            return x.MapErr((e) => (IIoError)e);
        }

        async UniTask<Result<ReaderBuffSegm<byte>, IIoError>> IBuffRx<byte>.PeekAsync
            ( NUsize offset
            , Demand demand
            , CancellationToken token)
        {
            var x = await this.PeekAsync(offset, demand, token);
            return x.MapErr((e) => (IIoError)e);
        }

        async UniTask<Result<WriterBuffSegm<byte>, IIoError>> IBuffTx<byte>.WriteAsync
            ( Demand demand
            , CancellationToken token)
        {
            var x = await this.WriteAsync(demand, token);
            return x.MapErr((e) => (IIoError)e);
        }

        #endregion

        #region IDisposable, IDisposeAsync Destructor

        private void Dispose_(bool isDisposing)
        {
            if (this.isDisposed_)
                return;
            if (isDisposing)
            {
                try
                {
                    this.cts_.Cancel();
                    this.cts_.Dispose();
                    this.tx_.Dispose();
                    this.rx_.Dispose();
                }
                finally
                {
                    this.isDisposed_ = true;
                    GC.SuppressFinalize(this);
                }
            }
        }

        public void Dispose()
            => this.Dispose_(true);

        ~BufferedSocket()
            => this.Dispose_(false);

        #endregion
    }

    public static class BuffSegmSocketExtensions
    {
        public static async UniTask<Socket> AcceptAsync(this Socket server, CancellationToken token = default)
        {
            var tcs = new TaskCompletionSource<Socket>();
            using var args = new SocketAsyncEventArgs();
            args.Completed += (s, e) =>
            {
                if (e.SocketError == SocketError.Success)
                    tcs.TrySetResult(e.AcceptSocket);
                else
                    tcs.TrySetException(new SocketException((int)e.SocketError));
            };
            if (!server.AcceptAsync(args))
                return args.AcceptSocket;
            return await tcs.Task.AttachExternalCancellation(token);
        }

        private static void HandleSocketWithEvent_(object sender, SocketAsyncEventArgs e, TaskCompletionSource<NUsize> tcs)
        {
            if (e.SocketError == SocketError.Success)
                tcs.TrySetResult((NUsize)e.BytesTransferred);
            else
                tcs.TrySetException(new SocketException((int)e.SocketError));
        }
    }

    public static class BufferedSocketCachedProxyExtensiosn
    {
        public static TxProxy<byte> GetCachedTxProxy(this BufferedSocket buffSocket)
            => buffSocket.GetCachedTxProxy<BufferedSocket, byte>();

        public static RxProxy<byte> GetCachedRxProxy(this BufferedSocket buffSocket)
            => buffSocket.GetCachedRxProxy<BufferedSocket, byte>();
    }
}
