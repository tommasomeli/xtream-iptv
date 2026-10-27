using System;
using MediaBrowser.Model.Logging;
using XtreamIptv.Logging;

namespace Emby.XtreamIptv
{
    /// <summary>Adapts Emby's ILogger to the shared ILog.</summary>
    public sealed class EmbyLog : ILog
    {
        private readonly ILogger _logger;

        public EmbyLog(ILogger logger)
        {
            _logger = logger;
        }

        public void Debug(string message, params object[] paramList) => _logger.Debug(message, paramList);

        public void Info(string message, params object[] paramList) => _logger.Info(message, paramList);

        public void Warn(string message, params object[] paramList) => _logger.Warn(message, paramList);

        public void Error(string message, params object[] paramList) => _logger.Error(message, paramList);

        public void ErrorException(string message, Exception exception, params object[] paramList) => _logger.ErrorException(message, exception, paramList);
    }
}
