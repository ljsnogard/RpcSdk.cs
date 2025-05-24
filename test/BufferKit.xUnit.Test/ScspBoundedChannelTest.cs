namespace NsBufferKit.xUnit.Test
{
    using Cysharp.Threading.Tasks;

    using Xunit.Abstractions;

    public sealed class ScspBoundedChannelTest
    {
        // private readonly ITestOutputHelper output_;

        private static readonly TimeSpan ALLOWANCE_TIMESPAN = TimeSpan.FromSeconds(2);
        
        // public ScspBoundedChannelTest(ITestOutputHelper output)
        //     => this.output_ = output;

        [Fact]
        public async Task ConsumerProducerShouldBePaired()
        {
            var channel = new ScspBoundedChannel<byte>(4);
            var optConsumer = await channel.GetConsumerAsync();
            Assert.True(optConsumer.IsSome(out var consumer));
            var optProducer = await channel.GetProducerAsync();
            Assert.True(optProducer.IsSome(out var producer));

            Assert.True(consumer.IsPairedWith(producer));
            Assert.True(producer.IsPairedWith(consumer));
        }

        [Theory]
        [InlineData(1u)]
        [InlineData(4u)]
        public async Task ConsumerShouldDequeueProduction(uint capacity)
        {
            var channel = new ScspBoundedChannel<uint>(capacity);
            var optConsumer = await channel.GetConsumerAsync();
            if (!optConsumer.IsSome(out var consumer))
                throw new Exception();
            var optProducer = await channel.GetProducerAsync();
            if (!optProducer.IsSome(out var producer))
                throw new Exception();
            for (var i = 0u; i < channel.Capacity; ++i)
            {
                var x = await producer.EnqueueAsync(i);
                Assert.True(x);
            }
            for (var i = 0u; i < channel.Capacity; ++i)
            {
                var optX = await consumer.DequeueAsync();
                Assert.True(optX.IsSome(out var x));
                Assert.Equal(i, x.GetValue());
                x.Dispose();
            }
        }

        [Fact]
        public async Task ConcurrentConsumerShouldNotBeAvailable()
        {
            var channel = new ScspBoundedChannel<byte>(1);
            var optConsumer1 = await channel.GetConsumerAsync();
            Assert.True(optConsumer1.IsSome(out var consumer1));

            var optProducer = await channel.GetProducerAsync();
            Assert.True(optProducer.IsSome(out var producer));

            Assert.True(producer.IsPairedWith(consumer1));

            var getConsumer2Task = channel.GetConsumerAsync().AsTask();
            await Task.Delay(ALLOWANCE_TIMESPAN);
            Assert.False(getConsumer2Task.IsCompleted);

            consumer1.Dispose();
            var completed = Task.WhenAny(getConsumer2Task, Task.Delay(ALLOWANCE_TIMESPAN));
            Assert.True(object.ReferenceEquals(await completed, getConsumer2Task));
            var optConsumer2 = await getConsumer2Task;
            Assert.True((optConsumer2.IsSome(out var consumer2)));
            Assert.True(producer.IsPairedWith(consumer2));
        }
        
        [Fact]
        public async Task ConcurrentProducerShouldNotBeAvailable()
        {
            var channel = new ScspBoundedChannel<byte>(1);
            var optProducer1 = await channel.GetProducerAsync();
            Assert.True(optProducer1.IsSome(out var producer1));

            var optConsumer = await channel.GetConsumerAsync();
            Assert.True(optConsumer.IsSome(out var consumer));

            Assert.True(consumer.IsPairedWith(producer1));

            var getProducer2Task = channel.GetProducerAsync().AsTask();
            await Task.Delay(ALLOWANCE_TIMESPAN);
            Assert.False(getProducer2Task.IsCompleted);

            producer1.Dispose();
            var completed = Task.WhenAny(getProducer2Task, Task.Delay(ALLOWANCE_TIMESPAN));
            Assert.True(object.ReferenceEquals(await completed, getProducer2Task));
            var optProducer2 = await getProducer2Task;
            Assert.True(optProducer2.IsSome(out var producer2));
            Assert.True(consumer.IsPairedWith(producer2));
        }

        [Theory]
        [InlineData(1u)]
        [InlineData(2u)]
        [InlineData(4u)]
        public async Task ProducerShouldBeWokenAfterConsuming(uint capacity)
        {
            // Console.SetOut(new UnitTestConsoleWriter(this.output_));

            var channel = new ScspBoundedChannel<uint>(capacity);
            var optProducer = await channel.GetProducerAsync();
            Assert.True(optProducer.IsSome(out var producer));
            var optConsumer = await channel.GetConsumerAsync();
            Assert.True(optConsumer.IsSome(out var consumer));

            for (var i = 0u; i < channel.Capacity; ++i)
            {
                var x = await producer.EnqueueAsync(i);
                Assert.True(x);
            }

            var blockedEnqueueTask = producer.EnqueueAsync(capacity).AsTask();
            await Task.Delay(ALLOWANCE_TIMESPAN);
            Assert.False(blockedEnqueueTask.IsCompleted);

            for (var i = 0u; i < channel.Capacity; ++i)
            {
                var optX = await consumer.DequeueAsync();
                Assert.True(optX.IsSome(out var x));
                x.Dispose();
            }

            var t = await Task.WhenAny(Task.Delay(ALLOWANCE_TIMESPAN), blockedEnqueueTask);
            Assert.True(Object.ReferenceEquals(t, blockedEnqueueTask));
        }

        [Theory]
        [InlineData(1u)]
        [InlineData(2u)]
        [InlineData(4u)]
        public async Task ConsumerShouldBeWokenAfterProducing(uint capacity)
        {
            // Console.SetOut(new UnitTestConsoleWriter(this.output_));

            var channel = new ScspBoundedChannel<uint>(capacity);
            var optProducer = await channel.GetProducerAsync();
            Assert.True(optProducer.IsSome(out var producer));
            var optConsumer = await channel.GetConsumerAsync();
            Assert.True(optConsumer.IsSome(out var consumer));

            for (var i = 0u; i < channel.Capacity; ++i)
            {
                var x = await producer.EnqueueAsync(i);
                Assert.True(x);
            }
            for (var i = 0u; i < channel.Capacity; ++i)
            {
                var optX = await consumer.DequeueAsync();
                Assert.True(optX.IsSome(out var x));
                x.Dispose();
            }

            var blockedDequeueTask = consumer.DequeueAsync().AsTask();
            await Task.Delay(ALLOWANCE_TIMESPAN);
            Assert.False(blockedDequeueTask.IsCompleted);

            var lastEnqueue = await producer.EnqueueAsync(capacity);
            Assert.True(lastEnqueue);

            Task t = await Task.WhenAny(Task.Delay(ALLOWANCE_TIMESPAN), blockedDequeueTask);
            Assert.True(t == blockedDequeueTask);
        }
    }
}
