namespace S2_ServerDemo
{
    using System.Threading;

    using Cysharp.Threading.Tasks;

    public interface IApp
    {
        public UniTask RunAsync(string[] args, CancellationToken token = default);
    }
}
