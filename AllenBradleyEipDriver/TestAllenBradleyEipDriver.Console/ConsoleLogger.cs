using Microsoft.Extensions.Logging;
using System;

namespace TestAllenBradleyEipDriver.Console
{
    /// <summary>
    /// 演示 ILogger 注入的最小实现：所有级别的日志直接打到控制台。
    /// 生产接入方应替换为 Microsoft.Extensions.Logging 生态中的正式
    /// Provider（NLog / Serilog / log4net.Microsoft.Extensions 等）。
    /// </summary>
    sealed class ConsoleLogger : ILogger
    {
        public IDisposable BeginScope<TState>(TState state)
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception exception, Func<TState, Exception, string> formatter)
        {
            global::System.Console.WriteLine("[{0:HH:mm:ss.fff}] [{1}] {2}",
                DateTime.Now, logLevel, formatter(state, exception));
        }
    }
}
