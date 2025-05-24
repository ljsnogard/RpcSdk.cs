namespace NsBufferKit
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    using NsAnyLR;

    using LoggingSdk;

    public delegate ref readonly U FnMapRefWithRef<T, U>(in T t)
        where T : struct
        where U : struct;

    public delegate ref U FnMapMutWithRefMut<T, U>(ref T t)
        where T : struct
        where U : struct;

    public delegate U FnMapWithRef<T, U>(in T t)
        where T : struct;

    public delegate void FnInit<T>(ref T t) where T : struct;

    /// <summary>
    /// A thread safe object pool.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public sealed partial class Arena<T> where T : struct
    {
        private readonly Dictionary<int, Chunk> workingChunks_;

        private readonly Dictionary<int, Chunk> drainedChunks_;

        private readonly uint chunkCapacity_;

        private readonly object mutex_;

        public Arena(uint chunkCapacity)
        {
            this.workingChunks_ = new();
            this.drainedChunks_ = new();
            this.chunkCapacity_ = chunkCapacity;
            this.mutex_ = new();
        }

        public Result<Token, CancellationToken> TryAllocate(
            FnInit<T> init,
            CancellationToken token = default)
        {
            lock (this.mutex_)
            {
                while (!token.IsCancellationRequested)
                {
                    var dcdc = 0; // drained chunks discoverd count
                    Span<int> fullChunksKeys = stackalloc int[this.workingChunks_.Count];
                    foreach (var kv in this.workingChunks_)
                    {
                        var chunk = kv.Value;
                        var index = chunk.Allocate(out var freeCount, token);
                        if (index != Chunk.NO_FREE)
                        {
                            var r = Token.Create(chunk, index);
                            init(ref r.ByMut());
                            return Result.Ok(r);
                        }
                        if (freeCount == 0)
                        {
                            fullChunksKeys[dcdc] = kv.Key;
                            dcdc++;
                        }
                    }
                    var rcdc = 0; // reworkable chunks discovered count
                    Span<int> reworkChunksKeys = stackalloc int[this.drainedChunks_.Count];
                    foreach (var kv in this.drainedChunks_)
                    {
                        var chunk = kv.Value;
                        if (chunk.FreeCount < chunk.Capacity / 2)
                            continue;
                        reworkChunksKeys[rcdc] = kv.Key;
                        rcdc++;
                    }
                    if (dcdc > 0)
                    {
                        for (int j = 0; j < dcdc; j++)
                        {
                            var key = fullChunksKeys[j];
                            if (this.workingChunks_.Remove(key, out var chunk))
                                this.drainedChunks_.Add(key, chunk);
                        }
                    }
                    if (rcdc > 0)
                    {
                        for (int j = 0; j < rcdc; j++)
                        {
                            var key = reworkChunksKeys[j];
                            if (this.drainedChunks_.Remove(key, out var chunk))
                                this.workingChunks_.Add(key, chunk);
                        }
                    }
                    else
                    {
                        var chunk = Chunk.Create(this.chunkCapacity_);
                        var key = chunk.GetHashCode();
                        this.workingChunks_.Add(key, chunk);
                    }
                }
                return Result.Err(token);
            }
        }
    }

    public sealed partial class Arena<T> where T : struct
    {
        private sealed class Chunk
        {
            private readonly Memory<Block> blocks_;

            private SpinLock spinlock_;

            private uint freeCount_;

            private int lastFree_;

            public const int NO_FREE = -1;

            public Chunk(Memory<Block> blocks)
            {
                this.blocks_ = blocks;
                this.spinlock_ = new SpinLock();
                this.freeCount_ = (uint)blocks.Length;
                this.lastFree_ = 0;
            }

            public static Chunk Create(NUsize capacity)
            {
                if (!capacity.TryInto(out int i32Capacity))
                    throw new ArgumentOutOfRangeException(nameof(capacity));

                var blocks = new Memory<Block>(new Block[i32Capacity]);
                var blocksSpan = blocks.Span;
                for (var i = 0; i < blocks.Length; ++i)
                {
                    ref var block = ref blocksSpan[i];
                    if (i < blocks.Length - 1)
                        block.NextFree = i + 1;
                    else
                        block.NextFree = NO_FREE;
                }
                return new Chunk(blocks);
            }

            public int Allocate(out uint freeCount, CancellationToken token = default)
            {
                var acquired = false;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        this.spinlock_.TryEnter(ref acquired);
                        if (!acquired)
                            continue;
                        if (this.lastFree_ == NO_FREE)
                            break;

                        ref var block = ref this.blocks_.Span[this.lastFree_];
                        this.lastFree_ = block.NextFree;
                        block.NextFree = Block.ALLOCATED;
                        this.freeCount_--;
                        break;
                    }
                    catch (Exception e)
                    {
                        Logger.Shared.Error($"[{nameof(Arena<T>)}.{nameof(Chunk)}.{nameof(Allocate)}] {e}, lastFree: {this.lastFree_}");
                        throw;
                    }
                    finally
                    {
                        if (acquired)
                            this.spinlock_.Exit();
                    }
                }
                if (this.lastFree_ == NO_FREE)
                    freeCount = 0;
                else
                    freeCount = this.freeCount_;

#if DEBUG
                Logger.Shared.Debug($"[{nameof(Arena<T>)}.{nameof(Chunk)}.{nameof(Allocate)}] chunk({this.GetHashCodeStrX8()}), freeCount: {freeCount}, capacity: {this.Capacity}");
#endif

                return this.lastFree_;
            }

            public bool TryDeallocate(int index, out uint freeCount, CancellationToken token = default)
            {
                if (index < 0 || index >= this.Capacity)
                {
                    freeCount = this.freeCount_;
                    return false;
                }
                var acquired = false;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        this.spinlock_.TryEnter(ref acquired);
                        if (!acquired)
                            continue;
                        ref var block = ref this.blocks_.Span[index];
                        if (block.NextFree != Block.ALLOCATED)
                        {
                            freeCount = this.freeCount_;
                            Logger.Shared.Warn($"[{nameof(Arena<T>)}.{nameof(Chunk)}.{nameof(TryDeallocate)}] chunk({this.GetHashCodeStrX8()}) invalid index: {index}");
                            return false;
                        }
                        block.NextFree = this.lastFree_;
                        this.lastFree_ = index;
                        this.freeCount_ ++;
                        break;
                    }

                    freeCount = this.freeCount_;
#if DEBUG
                    Logger.Shared.Debug($"[{nameof(Arena<T>)}.{nameof(Chunk)}.{nameof(TryDeallocate)}] chunk({this.GetHashCodeStrX8()}), index: {index}, freeCount: {freeCount}, capacity: {this.Capacity}");
#endif

                    return true;
                }
                finally
                {
                    if (acquired)
                        this.spinlock_.Exit();
                }
            }

            public uint Capacity
                => (uint)this.blocks_.Length;

            public uint FreeCount
                => this.freeCount_;

            public ReadOnlyMemory<Block> ReadOnlyBlocks
                => this.blocks_;

            public Memory<Block> Blocks
                => this.blocks_;
        }

        private struct Block
        {
            /// <summary>
            /// 下一个未被使用的 Block 的位置
            /// </summary>
            public int NextFree;

            public T Item;

            public const int ALLOCATED = int.MinValue;
        }

        public struct Token : IDisposable
        {
            private readonly Chunk chunk_;

            private int index_;

            private Token(Chunk chunk, int index)
            {
                this.chunk_ = chunk;
                this.index_ = index;
            }

            internal static Token Create(object chunkObj, int index)
            {
                if (chunkObj is not Chunk chunk)
                    throw new ArgumentException(message: "chunk type expected.", paramName: nameof(chunkObj));
                if (index < 0 || index >= chunk.Capacity)
                    throw new ArgumentOutOfRangeException(message: $"{index}", paramName: nameof(index));
                return new Token(chunk, index);
            }

            public readonly bool IsDisposed
                => this.index_ < 0;

            public readonly ref T ByMut()
                => ref this.chunk_.Blocks.Span[this.index_].Item;

            public readonly ref readonly T ByRef()
                => ref this.chunk_.ReadOnlyBlocks.Span[this.index_].Item;

            public ref readonly U Map<U>(FnMapRefWithRef<T, U> map)
                where U : struct
            {
                ref readonly var t = ref this.ByRef();
                return ref map(in t);
            }

            public ref U Map<U>(FnMapMutWithRefMut<T, U> map)
                where U : struct
            {
                ref var t = ref this.ByMut();
                return ref map(ref t);
            }

            public U Map<U>(FnMapWithRef<T, U> map)
            {
                ref readonly var t = ref this.ByRef();
                return map(in t);
            }

            public U Map<U>(Func<T, U> map)
            {
                ref readonly var t = ref this.ByRef();
                return map(t);
            }

            public void Dispose()
            {
                if (this.IsDisposed)
                    return;
                try
                {
                    this.chunk_.TryDeallocate(this.index_, out _);
                }
                finally
                {
                    this.index_ = Chunk.NO_FREE;
                }
            }
        }
    }
}