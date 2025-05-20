namespace LoggingSdk
{
    using System;
#if DOTNET
    using Serilog;

    public class SerilogLogger : ILogger
    {
        private readonly Serilog.ILogger logger_;

        public SerilogLogger()
        {
            logger_ = new LoggerConfiguration()
                .WriteTo.Console()
                .WriteTo.File("logs/server.log", rollingInterval: RollingInterval.Day)
                .CreateLogger();
        }

        #if DEBUG

        public void Info(string message)
            => System.Console.Out.WriteLine($"{DateTimeOffset.Now.ToString("O")}[{nameof(Info)}] {message}");

        public void Warn(string message)
            => System.Console.Out.WriteLine($"{DateTimeOffset.Now.ToString("O")}[{nameof(Warn)}] {message}");

        public void Error(string message)
            => System.Console.Out.WriteLine($"{DateTimeOffset.Now.ToString("O")}[{nameof(Error)}] {message}");

        public void Debug(string message)
            => System.Console.Out.WriteLine($"{DateTimeOffset.Now.ToString("O")}[{nameof(Debug)}] {message}");

        #else

        public void Info(string message)
            => logger_.Information(message);

        public void Warn(string message)
            => logger_.Warning(message);

        public void Error(string message)
            => logger_.Error(message);

        public void Debug(string message)
            => logger_.Debug(message);

        #endif
    }
#endif
}
