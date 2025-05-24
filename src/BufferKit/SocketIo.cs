namespace NsBufferKit
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;

    using Cysharp.Threading.Tasks;

    using NsAnyLR;

    using LoggingSdk;

    public readonly struct SocketIoError : IIoError
    {
        private readonly Result<uint, SocketError> data_;

        public SocketIoError(uint errCode)
            => this.data_ = Result.Ok(errCode);

        public SocketIoError(SocketError err)
            => this.data_ = Result.Err(err);

        public Result<uint, SocketError> Data
            => this.data_;

        public Exception AsException()
        {
            if (this.data_.TryOk(out var errCode, out var sockErr))
            {
                var m = errCode switch
                {
                    ERR_CLOSED => nameof(ERR_CLOSED),
                    ERR_DISPOSED => nameof(ERR_DISPOSED),
                    _ => "Unknown",
                };
                return new Exception(m);
            }
            else
            {
                return new Exception(sockErr.ToString());
            }
        }

        public const uint ERR_CLOSED = 0;

        public const uint ERR_DISPOSED = 1;
    }

    public abstract class SocketIo
    {
        private readonly Socket socket_;

        private readonly EndPoint localEp_;

        private readonly EndPoint remoteEp_;

        private uint disposeFlags_;

        internal protected const uint K_INPUT_DISPOSE = 2u;

        internal protected const uint K_OUTPUT_DISPOSE = 3u;

        internal protected const uint K_DUAL_DISPOSE = K_INPUT_DISPOSE * K_OUTPUT_DISPOSE;

        internal protected SocketIo(Socket socket)
        {
            this.socket_ = socket;
            this.localEp_ = socket.LocalEndPoint;
            this.remoteEp_ = socket.RemoteEndPoint;
            this.disposeFlags_ = 1u;
        }

        internal protected Socket Socket
            => this.socket_;

        internal static void HandleSocketWithEvent_(object sender, SocketAsyncEventArgs e, TaskCompletionSource<NUsize> tcs)
        {
            if (e.SocketError == SocketError.Success)
                tcs.TrySetResult((NUsize)e.BytesTransferred);
            else
                tcs.TrySetException(new SocketException((int)e.SocketError));
        }

        internal protected void DisposeSocket_(uint disposeFlag)
        {
            if (this.disposeFlags_ % disposeFlag == 0)
                return;
            try
            {
                this.disposeFlags_ *= disposeFlag;
            }
            finally
            {
                if (this.disposeFlags_ == K_DUAL_DISPOSE)
                {
                    this.socket_.Dispose();
                    GC.SuppressFinalize(this);
                }
            }
        }

        public EndPoint RemoteEndPoint
            => this.remoteEp_;

        public EndPoint LocalEndPoint
            => this.localEp_;

        public static (SocketOutput, SocketInput) Split(Socket socket)
        {
            var output = new SocketOutput(socket);
            var input = new SocketInput(socket);
            return (output, input);
        }

        public static UniTask<(SocketOutput, SocketInput)> ConnectAsync
            ( EndPoint endpoint
            , CancellationToken token = default)
        {
            async UniTask<Socket> CreateSocketAsync_CapturingEndpoint_(CancellationToken tok)
                => await BufferedSocket.ConnectAsync(endpoint, tok);

            return SocketIo.FromSocketAsync(CreateSocketAsync_CapturingEndpoint_, token);
        }

        public static async UniTask<(SocketOutput, SocketInput)> FromSocketAsync
            ( Func<CancellationToken, UniTask<Socket>> createSocketAsync
            , CancellationToken token = default)
        {
            var isSocketCreated = false;
            Socket? socket = null;
            try
            {
                socket = await createSocketAsync(token);
                var input = new SocketInput(socket);
                var output = new SocketOutput(socket);
                isSocketCreated = true;
                return (output, input);
            }
            catch (Exception e)
            {
                Console.Out.WriteLine(e);
                throw;
            }
            finally
            {
                if (socket is Socket createdSocket && !isSocketCreated)
                    createdSocket.Dispose();
            }
        }
    }

    public sealed class SocketInput : SocketIo, IUnbufferedInput<byte>, IDisposable
    {
        private readonly AsyncMutex mutex_;

        private bool isClosed_;

        private bool isDisposed_;

        internal SocketInput(Socket socket) : base(socket)
        {
            this.mutex_ = new();
            this.isClosed_ = false;
            this.isDisposed_ = false;
        }

        public bool IsPairedWith(SocketOutput output)
            => object.ReferenceEquals(this.Socket, output.Socket);

        public async UniTask<Result<NUsize, SocketIoError>> ReadAsync(
            Memory<byte> target,
            CancellationToken token = default)
        {
            var log = Logger.Shared;
            if (this.isDisposed_)
                throw new Exception($"[{nameof(SocketInput)}.{nameof(ReadAsync)}] this ({this.GetHashCodeStrX8()}) is disposed.");

            if (this.isClosed_)
                return Result.Err(new SocketIoError(SocketIoError.ERR_CLOSED));

            Option<AsyncMutex.Guard> optGuard = Option.None();
            NUsize recvSize = NUsize.Zero;
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Result.Ok(recvSize);

                var tcs = new TaskCompletionSource<NUsize>();
                using var args = new SocketAsyncEventArgs();

                args.SetBuffer(target);
                args.Completed += (s, e) => SocketIo.HandleSocketWithEvent_(s, e, tcs);

                if (!this.Socket.ReceiveAsync(args))
                    recvSize = (NUsize)args.BytesTransferred;
                else
                    recvSize = await tcs.Task.AttachExternalCancellation(token);

                if (recvSize == NUsize.Zero)
                    this.isClosed_ = true;
#if DEBUG
                try
                {
                    var sentMem = target.Slice(0, args.BytesTransferred);
                    var jsonStr = System.Text.Encoding.UTF8.GetString(sentMem.Span);
                    var hexBuilder = new System.Text.StringBuilder();
                    for (var i = 0; i < sentMem.Length; ++i)
                        hexBuilder.Append($"{sentMem.Span[i]:X2} ");

                    log.Debug($"[{nameof(SocketInput)}.{nameof(ReadAsync)}](l: {this.LocalEndPoint}, r: {this.RemoteEndPoint}) recv {recvSize} bytes, cont: \n{jsonStr}\n{hexBuilder}\n");
                }
                finally
                { }
#endif
                return Result.Ok(recvSize);
            }
            catch (OperationCanceledException)
            {
                return Result.Ok(recvSize);
            }
            catch (Exception e)
            {
                log.Debug($"[{nameof(SocketInput)}.{nameof(ReadAsync)}](l: {this.LocalEndPoint}, r: {this.RemoteEndPoint}) exception: {e} {e.Message}");
                throw;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        async UniTask<Result<NUsize, IIoError>> IUnbufferedInput<byte>.ReadAsync(
            Memory<byte> target,
            CancellationToken token)
        {
            var x = await this.ReadAsync(target, token);
            return x.MapErr(e => (IIoError)e);
        }

        #region IDisposable

        public void Dispose()
            => this.Dispose_(true);

        ~SocketInput()
            => this.Dispose_(false);

        private void Dispose_(bool isDisposing)
        {
            if (this.isDisposed_)
                return;
            if (isDisposing)
            {
                try
                {
                    base.DisposeSocket_(K_INPUT_DISPOSE);
                }
                finally
                {
                    this.isDisposed_ = true;
                }
            }
            else
                Logger.Shared.Warn($"[{nameof(SocketInput)}.{nameof(Dispose_)}] isDisposing_ false");
        }

        #endregion
    }

    public sealed class SocketOutput : SocketIo, IUnbufferedOutput<byte>, IDisposable
    {
        private readonly AsyncMutex mutex_;

        private bool isClosed_;

        private bool isDisposed_;

        internal SocketOutput(Socket socket) : base(socket)
        {
            this.mutex_ = new();
            this.isDisposed_ = false;
        }

        public bool IsPairedWith(SocketInput input)
            => object.ReferenceEquals(this.Socket, input.Socket);

        public async UniTask<Result<NUsize, SocketIoError>> WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken token = default)
        {
            var log = Logger.Shared;
            if (this.isDisposed_)
                throw new Exception($"[{nameof(SocketInput)}.{nameof(WriteAsync)}] this ({this.GetHashCodeStrX8()}) is disposed.");

            if (this.isClosed_)
                return Result.Err(new SocketIoError(SocketIoError.ERR_CLOSED));

            Option<AsyncMutex.Guard> optGuard = Option.None();
            NUsize sentSize = NUsize.Zero;
            try
            {
                optGuard = await this.mutex_.AcquireAsync(token);
                if (!optGuard.IsSome(out var guard))
                    return Result.Ok(sentSize);

                var tcs = new TaskCompletionSource<NUsize>();
                using var args = new SocketAsyncEventArgs();

                // FIXME: 数组复制可能导致性能问题，但是只能传入 Memory<byte> 或者 byte[] 类型

                // Convert ReadOnlyMemory<byte> to Memory<byte> as SendAsync requires Memory<byte>
                Memory<byte> buffer = MemoryMarshal.AsMemory(source);

                // var buffer = source.Span.ToArray();
                args.SetBuffer(buffer);
                args.Completed += (s, e) => SocketIo.HandleSocketWithEvent_(s, e, tcs);

                // Send

                if (!this.Socket.SendAsync(args))
                    sentSize = (NUsize)args.BytesTransferred;
                else
                    sentSize = await tcs.Task.AttachExternalCancellation(token);

                if (sentSize == NUsize.Zero)
                    this.isClosed_ = true;
#if DEBUG
                try
                {
                    var recvCont = buffer.Slice(0, args.BytesTransferred);
                    var jsonStr = System.Text.Encoding.UTF8.GetString(recvCont.Span);
                    var hexBuilder = new System.Text.StringBuilder();
                    for (var i = 0; i < recvCont.Length; ++i)
                        hexBuilder.Append($"{recvCont.Span[i]:X2} ");

                    log.Debug($"[{nameof(SocketOutput)}.{nameof(WriteAsync)}](l: {this.LocalEndPoint}, r: {this.RemoteEndPoint}) sent {sentSize} bytes, cont:\n{jsonStr}\n{hexBuilder}\n");
                }
                finally
                { }
#endif
                return Result.Ok(sentSize);
            }
            catch (OperationCanceledException)
            {
                return Result.Ok(sentSize);
            }
            catch (Exception e)
            {
                log.Debug($"[{nameof(SocketOutput)}.{nameof(WriteAsync)}] {e}");
                throw;
            }
            finally
            {
                if (optGuard.IsSome(out var guard))
                    guard.Dispose();
            }
        }

        async UniTask<Result<NUsize, IIoError>> IUnbufferedOutput<byte>.WriteAsync
            ( ReadOnlyMemory<byte> source
            , CancellationToken token)
        {
            var x = await this.WriteAsync(source, token);
            return x.MapErr(e => (IIoError)e);
        }

        #region IDisposable

        public void Dispose()
            => this.Dispose_(true);

        ~SocketOutput()
            => this.Dispose_(false);

        private void Dispose_(bool isDisposing)
        {
            if (this.isDisposed_)
                return;
            if (isDisposing)
            {
                try
                {
                    base.DisposeSocket_(K_OUTPUT_DISPOSE);
                }
                finally
                {
                    this.isDisposed_ = true;
                }
            }
            else
                Logger.Shared.Warn($"[{nameof(SocketOutput)}.{nameof(Dispose_)}] isDisposing_ false");
        }

        #endregion
    }

    public static class SocketInputGetCachedProxyExtensions
    {
        public static InputProxy<byte> GetCachedProxy(this SocketInput input)
            => input.GetCachedProxy<SocketInput, byte>();
    }

    public static class SocketOutputGetCachedProxyExtensions
    {
        public static OutputProxy<byte> GetCachedProxy(this SocketOutput output)
            => output.GetCachedProxy<SocketOutput, byte>();
    }
}