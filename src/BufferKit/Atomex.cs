namespace NsBufferKit
{
    using System;
    using System.Threading;

    using NsAnyLR;

    public static class CmpXchResult
    {
        public static CmpXchResult<T> Succ<T>(T data)
            => CmpXchResult<T>.Succ(data);

        public static CmpXchResult<T> Fail<T>(T data)
            => CmpXchResult<T>.Fail(data);

        public static CmpXchResult<T> Unexpected<T>(T data)
            => CmpXchResult<T>.Unexpected(data);
    }

    public readonly struct CmpXchResult<T>
    {
        private enum State
        {
            Succ = 1,
            Fail = 0,
            Unexpected = -1,
        }

        private readonly State s_;

        private readonly T v_;

        private CmpXchResult(State s, T v)
        {
            this.s_ = s;
            this.v_ = v;
        }

        public static CmpXchResult<T> Succ(T data)
            => new CmpXchResult<T>(State.Succ, data);

        public static CmpXchResult<T> Fail(T data)
            => new CmpXchResult<T>(State.Fail, data);

        public static CmpXchResult<T> Unexpected(T data)
            => new CmpXchResult<T>(State.Unexpected, data);

        public T Data
            => this.v_;

        public bool IsSucc(out T data)
        {
            var match = this.s_ == State.Succ;
            data = this.v_;
            return match;
        }

        public bool IsFail(out T data)
        {
            var match = this.s_ == State.Fail;
            data = this.v_;
            return match;
        }

        public bool IsUnexpected(out T data)
        {
            var match = this.s_ == State.Unexpected;
            data = this.v_;
            return match;
        }

        public string ToString(Func<T, string>? fmt = null)
        {
            var name = this.s_ switch
            {
                State.Succ => nameof(Succ),
                State.Fail => nameof(Fail),
                State.Unexpected => nameof(Unexpected),
                _ => $"__Illegal({(int)this.s_})",
            };
            if (fmt is null)
                fmt = PrintValue;
            return $"{nameof(CmpXchResult<T>)}.{name}({fmt(this.v_)})";

            static string PrintValue(T t) {
                if (t is null)
                    return $"null({typeof(T).Name})";
                else
                    return t.ToString();
            }
        }

        public override string ToString()
            => this.ToString(fmt: null);
    }

    public struct AtomicU64Flags
    {
        private long cell_;

        public AtomicU64Flags(ulong data = 0uL)
            => this.cell_ = unchecked((long)data);

        public ulong Read()
            => unchecked((ulong) Interlocked.Read(ref this.cell_));

        public Result<ulong, ulong> CompareExchange(ulong expected, ulong desired)
        {
            var replaced = Interlocked.CompareExchange(
                ref this.cell_,
                value: unchecked((long)desired),
                comparand: unchecked((long)expected)
            );
            var x = unchecked((ulong)replaced);
            if (x == expected)
                return Result.Ok(x);
            else
                return Result.Err(x);
        }

        public CmpXchResult<ulong> TryOnceCompareExchange(
            ulong current,
            Func<ulong, bool> expect,
            Func<ulong, ulong> desire)
        {
            if (!expect(current))
                return CmpXchResult.Unexpected(current);
            var desired = desire(current);
            var x = this.CompareExchange(current, desired);
            if (x.TryOk(out var replaced, out var original))
                return CmpXchResult.Succ(replaced);
            else
                return CmpXchResult.Fail(original);
        }

        public CmpXchResult<ulong> TrySpinCompareExchange(
            Func<ulong, bool> expect,
            Func<ulong, ulong> desire,
            CancellationToken token = default)
        {
            var current = this.Read();
            while (true)
            {
                if (!expect(current))
                    return CmpXchResult.Unexpected(current);
                var res = this.TryOnceCompareExchange(current, expect, desire);
                if (res.IsSucc(out _) || res.IsUnexpected(out _))
                    return res;
                if (token.IsCancellationRequested)
                    return CmpXchResult.Fail(res.Data);
                if (res.IsFail(out current))
                    continue;
            }
        }
    }
}