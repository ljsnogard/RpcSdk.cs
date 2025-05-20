namespace BufferKit.xUnit.Test
{
    using System;
    using System.Threading.Tasks;

    public sealed class BuffSegmTest
    {
        [Fact]
        public void BuffSegmForwardShouldChangeLength()
        {
            const uint TOTAL_LEN = 64;
            var memory = new Memory<uint>(new uint[TOTAL_LEN]);
            for (var i = 0u; i < TOTAL_LEN; ++i)
                memory.Span[(int)i] = i;
            var random = Random.Shared;
            var l0 = random.Next(2, memory.Length);
            var l1 = TOTAL_LEN - l0;
            var s0 = memory.Slice(start: 0, length: l0);
            var s1 = memory.Slice(start: l0);
            
            var mr = new ReadOnlyMemory<ReadOnlyMemory<uint>>(new [] { (ReadOnlyMemory<uint>)s0, (ReadOnlyMemory<uint>)s1 } );
            using (var readerSegm = new ReaderBuffSegm<uint>(mr, Option.None, NoReclaim<uint>.Shared, 0, 0))
            {
                Assert.Equal((NUsize)TOTAL_LEN, readerSegm.Length);
                Assert.Equal((NUsize)TOTAL_LEN, readerSegm.Capacity);
                var data = readerSegm.GetUnreadSlices();
                {
                    var u = 0u;
                    for (var d = 0; d < data.Length; ++d)
                    {
                        var a = data.Span[d];
                        for (var i = 0; i < a.Length; ++i)
                        {
                            Assert.Equal(u, a.Span[i]);
                            u++;
                        }
                    }
                }
                var f0 = (NUsize)random.Next(1, l0);
                readerSegm.Forward(f0);
                Assert.Equal(f0, readerSegm.Offset);
                Assert.Equal(readerSegm.Capacity - f0, readerSegm.Length);
            }
            var mm = new ReadOnlyMemory<Memory<uint>>(new [] { s0, s1 } );
            using (var writerSegm = new WriterBuffSegm<uint>(mm, Option.None, NoReclaim<uint>.Shared, 0, 0))
            {
                Assert.Equal((NUsize)TOTAL_LEN, writerSegm.Length);
                Assert.Equal((NUsize)TOTAL_LEN, writerSegm.Capacity);
                var f0 = (NUsize)random.Next(1, l0);
                writerSegm.Forward(f0);
                Assert.Equal(writerSegm.Capacity - f0, writerSegm.Length);
            }
        }
    }
}
