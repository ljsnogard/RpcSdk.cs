namespace BufferKit.xUnit.Test
{
    public sealed class ArenaTest
    {
        public struct SampleData
        {
            public uint A;
            public int B;
        }

        public static ref uint ByRefSelectA(ref SampleData data)
            => ref data.A;

        public static ref readonly int ByRefSelectB(in SampleData data)
            => ref data.B;

        [Fact]
        public void ArenaSmoke()
        {
            var arena = new Arena<SampleData>(4);
            var tokensList = new List<Arena<SampleData>.Token>();
            for (int i = 0; i < 16; ++i)
            {
                var maybeToken = arena.TryAllocate((ref SampleData d) => { d.A = 100; d.B = -1; });
                Assert.True(maybeToken.TryOk(out var arenaToken, out _));
                ref var a = ref arenaToken.Map(ByRefSelectA);
                ref readonly var b = ref arenaToken.Map(ByRefSelectB);

                Assert.Equal(100u, a);
                Assert.Equal(-1, b);

                tokensList.Add(arenaToken);
            }
            tokensList.ForEach(token => token.Dispose());
        }

        [Fact]
        void ArenaMultithreadSmoke()
        {
            var arena = new Arena<SampleData>(4);
            var threads = new List<Thread>();
            for (var i = 0; i < System.Environment.ProcessorCount; ++i)
            {
                var thread = new Thread(Start);
                thread.Start();
                threads.Add(thread);
            }
            foreach (var thread in threads)
                thread.Join();
            return;

            void Start()
            {
                var tokensList = new List<Arena<SampleData>.Token>();
                for (int i = 0; i < 16; ++i)
                {
                    var maybeToken = arena.TryAllocate((ref SampleData d) => { d.A = 100; d.B = -1; });
                    Assert.True(maybeToken.TryOk(out var arenaToken, out _));
                    ref var a = ref arenaToken.Map(ByRefSelectA);
                    ref readonly var b = ref arenaToken.Map(ByRefSelectB);
                    tokensList.Add(arenaToken);
                }
                foreach (var token in tokensList)
                    token.Dispose();
            }
        }
    }
}