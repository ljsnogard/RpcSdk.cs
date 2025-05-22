using System;

namespace SerdesKit.Json
{
    public readonly struct JsonSerdesError : ISerdesError
    {
        private readonly string message_;

        public JsonSerdesError(string message)
            => this.message_ = message;

        public string Message
            => this.message_;

        public Exception AsException()
            => new Exception(this.message_);
    }
}