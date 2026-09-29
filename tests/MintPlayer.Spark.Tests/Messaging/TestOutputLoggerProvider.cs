using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// Writes warnings and errors from the messaging pipeline to the test's output, so a feeder that fails
/// every batch shows why instead of surfacing only as a timed-out wait.
/// </summary>
internal sealed class TestOutputLoggerProvider(ITestOutputHelper output, LogLevel minimum = LogLevel.Warning) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(output, minimum, categoryName);
    public void Dispose() { }

    private sealed class Logger(ITestOutputHelper output, LogLevel minimum, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try { output.WriteLine($"[{logLevel}] {category}: {formatter(state, exception)}{Describe(exception)}"); }
            catch (InvalidOperationException) { /* the test has finished; xunit refuses late output */ }
        }

        /// <summary>The exception and its inner chain, one line each: a subscriber error wraps the real cause.</summary>
        private static string Describe(Exception? exception)
        {
            var text = "";
            for (var e = exception; e is not null; e = e.InnerException)
                text += $" -- {e.GetType().Name}: {e.Message.Split('\n')[0].Trim()}";
            return text;
        }
    }
}
