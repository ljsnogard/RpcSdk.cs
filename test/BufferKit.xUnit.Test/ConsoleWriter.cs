namespace NsBufferKit.xUnit.Test
{
    using System.IO;
    using System.Text;

    using Xunit.Abstractions;

    public class UnitTestConsoleWriter : StringWriter
    {
        private ITestOutputHelper output;

        public UnitTestConsoleWriter(ITestOutputHelper output)
            => this.output = output;

        public override void WriteLine(string? m)
            => output.WriteLine(m);
        
    }

    public class RedirectOutput : TextWriter
    {
        private readonly ITestOutputHelper output_;

        public RedirectOutput(ITestOutputHelper output) : this(output, Encoding.UTF8)
        { }

        public RedirectOutput(ITestOutputHelper output, Encoding encoding)
        {
            this.output_ = output;
            this.Encoding = encoding;
        }

        public override Encoding Encoding { get; } // set some if required

        public override void WriteLine(string? value)
            => output_.WriteLine(value);
    }
}