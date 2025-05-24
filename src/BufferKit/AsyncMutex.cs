namespace NsBufferKit
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using NsAnyLR;

    public sealed class AsyncMutex
    {
        public struct Guard
        {
            private AsyncMutex? mutex_;

            internal Guard(AsyncMutex mutex)
                => this.mutex_ = mutex;

            public bool IsAcquiredFrom(AsyncMutex mutex)
                => this.mutex_ is AsyncMutex src && object.ReferenceEquals(src, mutex);

            public bool IsDisposed
                => this.mutex_ is null;

            public void Dispose()
            {
                try
                {
                    if (this.mutex_ is not AsyncMutex mutex)
                        return;
                    else
                        mutex.Release_();
                }
                finally
                {
                    this.mutex_ = null;
                }
            }
        }

        private readonly struct AtomicSpinlockGuard : IDisposable
        {
            private readonly AsyncMutex mutex_;

            private AtomicSpinlockGuard(AsyncMutex mutex)
                => this.mutex_ = mutex;

            public static Option<AtomicSpinlockGuard> Acquire
                (AsyncMutex mutex
                , CancellationToken token = default)
            {
                static bool Always(ulong s)
                    => true;

                while (!token.IsCancellationRequested)
                {
                    var cmpXchRes = mutex.flags_.TrySpinCompareExchange(
                        Always,
                        DesireQueueLocked,
                        token
                    );
                    if (cmpXchRes.IsSucc(out _))
                        return Option.Some<AtomicSpinlockGuard>(new(mutex));
                }
                return Option.None();
            }

            public void Dispose()
            {
                const uint MAX_TRY = 100u;
                var c = 0u;
                while (c < MAX_TRY)
                {
                    var xchRes = this.mutex_.flags_.TrySpinCompareExchange(
                        ExpectQueueLocked,
                        DesireQueueNotLocked
                    );
                    if (xchRes.IsSucc(out _))
                        return;
                    ++c;
                }
                throw new Exception("failed in unlocking");
            }
        }

        private readonly LinkedList<UniTaskCompletionSource<Option<Guard>>> queue_;

        private AtomicU64Flags flags_;

        #region 

        private const ulong K01_B63_ACQUIRED = 1uL << 63;

        private const ulong K01_B62_ENQUEUED = 1uL << 62;

        private const ulong K01_B61_Q_LOCKED = 1ul << 61;

        private static bool ExpectAcquired(ulong s)
            => (s & K01_B63_ACQUIRED) == K01_B63_ACQUIRED;

        private static bool ExpectNotAcquired(ulong s)
            => !ExpectAcquired(s);

        private static bool ExpectEnqueued(ulong s)
            => (s & K01_B62_ENQUEUED) == K01_B62_ENQUEUED;

        private static bool ExpectNotEnqueued(ulong s)
            => !ExpectEnqueued(s);

        private static bool ExpectQueueLocked(ulong s)
            => (s & K01_B61_Q_LOCKED) == K01_B61_Q_LOCKED;

        private static bool ExpectQueueNotLocked(ulong s)
            => !ExpectQueueLocked(s);

        private static bool ExpectNoContent(ulong s)
            => s == 0uL;

        private static ulong DesireNoContent(ulong s)
            => 0uL;

        private static ulong DesireAcquired(ulong s)
            => s | K01_B63_ACQUIRED;

        private static ulong DesireNotAcquired(ulong s)
            => s & (~K01_B63_ACQUIRED);

        private static ulong DesiredEnqueued(ulong s)
            => s | K01_B62_ENQUEUED;

        private static ulong DeisredNotEnqueued(ulong s)
            => s & (~K01_B62_ENQUEUED);

        private static ulong DesireQueueLocked(ulong s)
            => s | K01_B61_Q_LOCKED;

        private static ulong DesireQueueNotLocked(ulong s)
            => s & (~K01_B61_Q_LOCKED);

        #endregion

        public AsyncMutex()
        {
            this.queue_ = new();
            this.flags_ = new(0uL);
        }

        public Option<Guard> TryAcquire()
        {
            var s = this.flags_.Read();
            var x = this.flags_.TryOnceCompareExchange(s, ExpectNoContent, DesireAcquired);
            if (x.IsSucc(out s))
                return Option.Some(new Guard(this));
            return Option.None();
        }

        public async UniTask<Option<Guard>> AcquireAsync(CancellationToken token = default)
        {
            var optGuard = this.TryAcquire();
            if (optGuard.IsSome())
                return optGuard;

            if (token.IsCancellationRequested)
                return Option.None();

            LinkedListNode<UniTaskCompletionSource<Option<Guard>>> node;
            UniTaskCompletionSource<Option<Guard>> tcs;
            Option<AtomicSpinlockGuard> optSpinlockGuard = Option.None();
            try
            {
                optSpinlockGuard = AtomicSpinlockGuard.Acquire(this, token);
                if (!optSpinlockGuard.IsSome(out var spinlockGuard))
                    return Option.None();

                tcs = new UniTaskCompletionSource<Option<Guard>>();
                node = this.queue_.AddLast(tcs);

                this.flags_.TrySpinCompareExchange(
                    ExpectNotEnqueued,
                    DesiredEnqueued
                );
                Debug.Assert(ExpectEnqueued(this.flags_.Read()));
            }
            finally
            {
                if (optSpinlockGuard.IsSome(out var spinlockGuard))
                    spinlockGuard.Dispose();
                optSpinlockGuard = Option.None();
            }
            try
            {
                optGuard = await tcs.Task.AttachExternalCancellation(token);
                optSpinlockGuard = AtomicSpinlockGuard.Acquire(this);
                if (!optSpinlockGuard.IsSome(out var spinlockGuard))
                    throw new Exception();

                this.queue_.Remove(node);
                return optGuard;
            }
            finally
            {
                if (optSpinlockGuard.IsSome(out var spinlockGuard))
                    spinlockGuard.Dispose();
            }
        }

        public void Release_()
        {
            lock (this.queue_)
            {
                if (this.queue_.First is LinkedListNode<UniTaskCompletionSource<Option<Guard>>> next)
                {
                    var tcs = next.Value;
                    tcs.TrySetResult(Option.Some(new Guard(this)));
                    return;
                }
                else
                {
                    var x = this.flags_.TrySpinCompareExchange(ExpectAcquired, DesireNoContent);
                    if (x.IsSucc(out var s))
                        return;
                }
            }
        }
    }

    public static class TaskGuardExtensions
    {
        public readonly struct Ensured : IDisposable
        {
            public readonly bool IsGuardOwner;

            public readonly AsyncMutex.Guard Guard;

            public Ensured(AsyncMutex.Guard guard, bool isGuardOwner)
            {
                this.Guard = guard;
                this.IsGuardOwner = isGuardOwner;
            }

            public void Dispose()
            {
                if (this.IsGuardOwner)
                    this.Guard.Dispose();
            }
        }

        public static async UniTask<Ensured> EnsureGuardedAsync
            ( this Option<AsyncMutex.Guard> optTaskGuard
            , AsyncMutex taskMutex
            , CancellationToken token = default)
        {
            var isGuardOwner = false;
            if (optTaskGuard.IsSome(out var guard))
            {
                if (!guard.IsAcquiredFrom(taskMutex))
                    throw new ArgumentException("Unmatch guard");
                return new(guard, isGuardOwner);
            }
            else
            {
                var optAcqGuard = await taskMutex.AcquireAsync(token);
                if (!optAcqGuard.IsSome(out guard))
                    throw new OperationCanceledException(token);
                isGuardOwner = true;
                return new(guard, isGuardOwner);
            }
        }
    }
}