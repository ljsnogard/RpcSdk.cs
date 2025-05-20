namespace RpcPeerComSdk
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    public readonly struct Header
    {
        private readonly string name_;

        private readonly ReadOnlyMemory<byte> cont_;

        private Header(string name, ReadOnlyMemory<byte> content)
        {
            this.name_ = name;
            this.cont_ = content;
        }

        public string Name
            => this.name_;

        public ReadOnlyMemory<byte> Content
            => this.cont_;

        public sealed class Builder
        {
            private string name_;

            private ReadOnlyMemory<byte> cont_;

            public Builder()
            {
                this.name_ = string.Empty;
                this.cont_ = ReadOnlyMemory<byte>.Empty;
            }

            public Builder SetName(string name)
            {
                if (string.IsNullOrEmpty(name))
                    throw new ArgumentException(message: "Header name cannot be null or empty", paramName: nameof(name));
                if (name.Any(Char.IsWhiteSpace))
                    throw new ArgumentException(message: $"Header name({name}) cannot contains white space", paramName: nameof(name));
                this.name_ = name;
                return this;
            }

            public Builder SetStringContent(string content)
            {
                if (string.IsNullOrEmpty(content))
                    throw new ArgumentException(message: $"Header content cannot be null or empty", paramName: nameof(content));
                var data = Encoding.UTF8.GetBytes(content);
                this.cont_ = data;
                return this;
            }

            public Builder SerializeContent<X>(X content)
                => throw new NotImplementedException();

            public Header Build()
            {
                if (string.IsNullOrEmpty(this.name_))
                    throw new InvalidOperationException("Build failed because header name not set");
                if (this.cont_.IsEmpty)
                    throw new InvalidOperationException("Build failed because header content is empty");
                return new Header(this.name_, this.cont_);
            }
        }
    }

    public readonly struct NoHeaders: IAsyncEnumerable<Header>
    {
        private static async IAsyncEnumerable<Header> YieldNothing()
        {
            await UniTask.Yield();
            yield break;
        }

        public static readonly NoHeaders Instance = new NoHeaders();

        public IAsyncEnumerator<Header> GetAsyncEnumerator(CancellationToken token = default)
            => YieldNothing().GetAsyncEnumerator(token);
    }
}
