namespace BufferKit.xUnit.Test
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using Xunit.Abstractions;

    public sealed class RingBufferTest
    {
        private readonly ITestOutputHelper output_;

        public RingBufferTest(ITestOutputHelper output)
            => this.output_ = output;

        [Fact]
        public async Task RingBufferIoShouldReturnNoGreaterLengthThanDemanded()
        {
            Console.SetOut(new UnitTestConsoleWriter(this.output_));

            var ringBuff = new RingBuffer<byte>(16);
            for (uint i = 1; i < ringBuff.Capacity; i++)
            {
                var txWriteRes = await ringBuff.WriteAsync(Demand.Exactly(i));
                if (!txWriteRes.TryOk(out var txSegm, out var error))
                    throw error.AsException();
                NUsize txLen = 0;
                foreach (var txMem in txSegm.EnumerateSpan())
                {
                    // 推进写者位置
                    var writtenSize = txMem.NUsizeLength();
                    txLen += writtenSize;
                    txSegm.Forward(writtenSize);
                }
                txSegm.Dispose();
                Assert.True(txLen <= i);

                var rxReadRes = await ringBuff.ReadAsync(Demand.AtLeast(i));
                if (!rxReadRes.TryOk(out var rxSegm, out error))
                    throw error.AsException();
                NUsize rxLen = 0;
                foreach (var rxMem in rxSegm.EnumerateSpan())
                {
                    var readSize = rxMem.NUsizeLength();
                    rxLen += readSize;
                    rxSegm.Forward(readSize);
                }
                rxSegm.Dispose();
                Assert.True(rxLen <= i);
            }
        }

        [Fact]
        public async Task ReaderSegmentCopyToSegmentAsyncShouldUpdateOffset()
        {
            var ringBuff = new RingBuffer<byte>(16);
            var rx = ringBuff.GetCachedRxProxy();
            var tx = ringBuff.GetCachedTxProxy();

            var initCont = new ReadOnlyMemory<byte>(new byte[8]);
            var maybeDataLen = await tx.DumpAsync(initCont);
            if (!maybeDataLen.TryOk(out var dataLen, out var txErr))
                throw txErr.AsException();

            var readDemand = Demand.AtLeast(4);
            var readRes = await rx.ReadAsync(Demand.AtLeast(4));
            if (!readRes.TryOk(out var readerSegment, out var rxErr))
                throw rxErr.AsException();

            var readerMem = readerSegment.GetUnreadSlices().Span[0];
            Assert.True(readerMem.NUsizeLength() >= readDemand.Floor.Unwrap());
            
            var writeRes = await tx.WriteAsync(Demand.AtLeast(2));
            if (!writeRes.TryOk(out var writerSegment, out var writerErr))
                throw writerErr.AsException();

            var writerMem = writerSegment.GetUnwrittenSlices().Span[0];
            var writerSegmentLen = writerSegment.Length;
            var fillRes = await readerSegment.FillAsync(writerSegment);
            if (!fillRes.TryOk(out var copyLen, out var segmErr))
                throw segmErr.AsException();

            writerSegment.Dispose();
            readerSegment.Dispose();

            Assert.Equal(copyLen, writerSegmentLen);
        }

        [Fact]
        public async Task RingBufferShouldWorkWithLeastCapacity()
        {
            Console.SetOut(new UnitTestConsoleWriter(this.output_));

            var ringBuffer = new RingBuffer<byte>(1);
            var maybe = ringBuffer.TrySplit();
            if (!maybe.TryOk(out var pair, out var _))
                throw new Exception();

            (var tx, var rx) = pair;
            NUsize count = 8;
            NUsize txCount = 0;
            NUsize rxCount = 0;

            var rxWork = async () =>
            {
                using (rx)
                {
                    while (rxCount < count)
                    {
                        var mem = new Memory<byte>(new [] { (byte)0 });
                        var tryRead = await rx.FillAsync(mem);
                        if (!tryRead.TryOk(out var readCount, out var error))
                            throw error.AsException();
                        Assert.Equal((uint)1, readCount);
                        Assert.Equal((byte)rxCount, mem.Span[0]);
                        rxCount += 1;
                    }
                    this.output_.WriteLine("rx loop exits.");
                }
            };
            var rxTask = rxWork();
            using (tx)
            {
                while (txCount < count)
                {
                    Assert.True(txCount.TryInto(out byte txCountU8));
                    var tryWrite = await tx.DumpAsync(new ReadOnlyMemory<byte>(new [] { txCountU8 }));
                    if (!tryWrite.TryOk(out var writeCount, out var error))
                        throw error.AsException();
                    Assert.Equal((uint)1, writeCount);
                    txCount += 1;
                }
                this.output_.WriteLine("tx loop exits.");
            }
            await rxTask;
        }

        [Theory]
        // [InlineData(1, 16)]
        [InlineData(4, 64)]
        // [InlineData(64, 256)]
        public async Task ReadDataOrderShouldBeTheSameAsWriteOrder_Random(int capacity, int maxNum)
        {
            // Console.SetOut(new UnitTestConsoleWriter(this.output_));

            var ringBuffer = new RingBuffer<int>((uint)capacity);

            var testDataSlices = new List<int[]>();
            var currNum = 0;
            while (currNum < maxNum)
            {
                var restLen = maxNum - currNum;
                int randLen;
                if (restLen == 1)
                    randLen = 1;
                else
                    randLen = new Random().Next(1, restLen);
                var slice = new int[randLen];
                for (var i = 0; i < randLen; i++)
                    slice[i] = currNum++;
                testDataSlices.Add(slice);
            }

            var tx = ringBuffer.GetCachedTxProxy();
            var txWorker = async () =>
            {
                foreach (var slice in testDataSlices)
                {
                    try
                    {
                        var sliceCont = string.Join(",", slice.Select(x => x.ToString()));
                        await Console.Out.WriteLineAsync($"TX: slice.Length={slice.Length}, cont=[{sliceCont}]");

                        // var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(maxNum * 100));
                        var d = await tx.DumpAsync(slice);
                        Assert.True(d.IsOk(out var dumpLen));
                        Assert.Equal((uint)slice.Length, dumpLen);
                    }
                    catch (Exception e)
                    {
                        await Console.Out.WriteLineAsync($"tx unexpected exception: {e}");
                        throw;
                    }
                }
            };

            var txTask = txWorker();
            var rx = ringBuffer.GetCachedRxProxy();
            var readNum = 0;
            while (readNum < maxNum)
            {
                try
                {
                    await Console.Out.WriteLineAsync($"RX: readNum={readNum}, maxNum={maxNum}");

                    var r = await rx.DequeueAsync();
                    Assert.True(r.IsOk(out var t));
                    Assert.Equal(readNum, t);
                    readNum++;
                }
                catch (Exception e)
                {
                    await Console.Out.WriteLineAsync($"rx unexpected exception: {e}");
                    throw;
                }
            };
            await txTask;
        }
    }
}
