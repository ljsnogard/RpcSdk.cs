namespace BufferKit.xUnit.Test
{
    public sealed class OptionTest
    {
        [Fact]
        public void OptionDefaultShouldBeNone()
        {
            Option<byte> opt = default;
            Assert.True(opt.IsNone());
            Assert.False(opt.IsSome(out _));
        }
    }
}