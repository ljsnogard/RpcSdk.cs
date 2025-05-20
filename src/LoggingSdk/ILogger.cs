namespace LoggingSdk
{
    using System;

    public interface ILogger
    {
        public void Info(string message);

        public void Warn(string message);

        public void Error(string message);

        public void Debug(string message);
    }

    public static class Logger
    {
        public static readonly ILogger Shared = Logger.Create();

        public static ILogger Create()
        {
#if UNITY
            return new UnityLogger();
#elif DOTNET
            return new SerilogLogger();
#else
            throw new NotSupportedException("Unknown environment");
#endif
        }
    }
}
