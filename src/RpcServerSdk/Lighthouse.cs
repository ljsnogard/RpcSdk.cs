namespace RpcServerSdk
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Net;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    /// <summary>
    /// 以 “.” 为分隔符的，符合 Uri DNS 规范的主机名
    /// </summary>
    public readonly struct HostEntry
    {
        /// <summary>
        /// 域名的分级表示形式，高级域名具有高索引。
        /// </summary>
        public readonly ReadOnlyMemory<string> Parts;

        private HostEntry(ReadOnlyMemory<string> parts)
            => this.Parts = parts;

        public static readonly char PartSeparator = '.';

        public static bool TryParse(string entryString, [NotNullWhen(true)]out HostEntry entry)
        {
            if (Uri.CheckHostName(entryString) != UriHostNameType.Dns)
            {
                entry = default;
                return false;
            }
            var parts = entryString.Split(PartSeparator, options: StringSplitOptions.None);
            entry = new HostEntry(parts);
            return true;
        }
    }

    public interface IQueryHostResult
    {
        public HostEntry Question { get; }

        public ReadOnlyMemory<IPEndPoint> Answers { get; }

        public DateTimeOffset Expiration { get; }
    }

    public interface ILighthouse
    {
        public UniTask<IQueryHostResult> QueryHostAsync(HostEntry hostEntry, CancellationToken token = default);
    }
}