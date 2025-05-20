#pragma warning disable CS8762 // Parameter must have a non-null value when exiting in some condition.
#pragma warning disable CS8604 // Possible null reference argument.
#pragma warning disable CS8601 // Possible null reference assignment.

namespace BufferKit
{
    using System;
    using System.Diagnostics.CodeAnalysis;

    public static class Option
    {
        public readonly struct InstanceNone
        { }

        public static Option<T> Some<T>(T data)
            => Option<T>.Some(data);

        public static InstanceNone None
            => new InstanceNone();
    }

    public readonly struct Option<T>
    {
        private readonly bool s_;

        private readonly T v_;

        private Option(T v)
        {
            this.s_ = true;
            this.v_ = v;
        }

        public static Option<T> Some(T data)
            => new Option<T>(data);

        public static implicit operator Option<T>(Option.InstanceNone none)
            => new Option<T>();

        public static implicit operator Option<T>(T data)
            => Option.Some(data);

        public Option<U> Map<U>(Func<T, U> mapFn)
        {
            if (this.s_)
                return new Option<U>(mapFn(this.v_));
            else
                return Option.None;
        }

        public T Unwrap(Func<System.Exception>? createException = null)
        {
            if (this.s_)
                return this.v_;
            if (createException is Func<System.Exception> f)
                throw f();
            else
                throw new Exception($"Unwrap on None");
        }

        public T SomeOrElse(Func<T> makeElse)
        {
            if (this.s_)
                return this.v_;
            else
                return makeElse();
        }

        public T SomeOrDefault(T val)
        {
            if (this.s_)
                return this.v_;
            else
                return val;
        }

        public bool IsSome()
            => this.s_;

        public bool IsSome([NotNullWhen(true)] out T data)
        {
            if (this.s_)
            {
                data = this.v_;
                return true;
            }
            else
            {

                data = default;
                return false;
            }
        }

        public bool IsNone()
            => this.s_ == false;
    }


    public static class Result
    {
        public readonly struct BuildErr<E>
        {
            public readonly E Err;

            public BuildErr(E err)
                => this.Err = err;
        }

        public readonly struct BuildOk<T>
        {
            public readonly T Ok;

            public BuildOk(T ok)
                => this.Ok = ok;
        }

        public static BuildOk<T> Ok<T>(T ok)
            => new BuildOk<T>(ok);

        public static BuildErr<E> Err<E>(E err)
            => new BuildErr<E>(err);
    }

    public readonly struct Result<T, E>
    {
        private readonly bool isOk_;

        private readonly T ok_;

        private readonly E err_;

        private Result(bool isOk, T ok, E err)
        {
            this.isOk_ = isOk;
            this.ok_ = ok;
            this.err_ = err;
        }

        public static Result<T, E> Ok(T data)
            => new Result<T, E>(true, data, default);

        public static Result<T, E> Err(E err)
            => new Result<T, E>(false, default, err);

        public static implicit operator Result<T, E>(Result.BuildOk<T> buildOk)
            => Result<T, E>.Ok(buildOk.Ok);

        public static implicit operator Result<T, E>(Result.BuildErr<E> buildErr)
            => Result<T, E>.Err(buildErr.Err);

        public Result<U, E> MapOk<U>(Func<T, U> mapOk)
        {
            if (this.isOk_)
                return new Result<U, E>(true, mapOk(this.ok_), default);
            else
                return new Result<U, E>(false, default, this.err_);
        }

        public Result<T, U> MapErr<U>(Func<E, U> mapErr)
        {
            if (this.isOk_)
                return new Result<T, U>(true, this.ok_, default);
            else
                return new Result<T, U>(false, default, mapErr(this.err_));
        }

        public bool IsOk()
            => this.isOk_;

        public bool IsOk([NotNullWhen(true)] out T ok)
            => this.TryOk(out ok, out _);

        public bool TryOk
            ([NotNullWhen(true)] out T ok
            , [NotNullWhen(false)] out E err)
        {
            if (this.isOk_)
            {
                ok = this.ok_;
                err = default;
                return true;
            }
            else
            {
                ok = default;
                err = this.err_;
                return false;
            }
        }

        public bool IsErr()
            => !this.IsOk();

        public bool IsErr([NotNullWhen(true)] out E err)
            => this.TryErr(out err, out _);

        public bool TryErr([NotNullWhen(true)] out E err, [NotNullWhen(false)] out T ok)
        {
            var x = this.TryOk(out ok, out err);
            return !x;
        }
    }

}

#pragma warning restore CS8601 // Possible null reference assignment.
#pragma warning restore CS8604 // Possible null reference argument.
#pragma warning restore CS8762 // Parameter must have a non-null value when exiting in some condition.