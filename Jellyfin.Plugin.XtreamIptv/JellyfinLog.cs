using System;
using System.Globalization;
using Microsoft.Extensions.Logging;
using XtreamIptv.Logging;

namespace Jellyfin.Plugin.XtreamIptv
{
    /// <summary>
    /// Adapts Microsoft.Extensions.Logging to the shared ILog. The shared code formats with {0}
    /// placeholders, so the text is composed first and logged as one message.
    /// </summary>
    public sealed class JellyfinLog : ILog
    {
        private readonly ILogger _logger;

        public JellyfinLog(ILogger logger)
        {
            _logger = logger;
        }

        public void Debug(string message, params object[] paramList)
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("{Message}", Format(message, paramList));
        }

        public void Info(string message, params object[] paramList) => _logger.LogInformation("{Message}", Format(message, paramList));

        public void Warn(string message, params object[] paramList) => _logger.LogWarning("{Message}", Format(message, paramList));

        public void Error(string message, params object[] paramList) => _logger.LogError("{Message}", Format(message, paramList));

        public void ErrorException(string message, Exception exception, params object[] paramList) => _logger.LogError(exception, "{Message}", Format(message, paramList));

        private static string Format(string message, object[] paramList)
        {
            if (paramList == null || paramList.Length == 0 || message == null) return message ?? string.Empty;
            try { return string.Format(CultureInfo.InvariantCulture, message, paramList); }
            catch (FormatException) { return message; }
        }
    }
}
