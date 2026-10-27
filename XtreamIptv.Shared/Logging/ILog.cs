using System;

namespace XtreamIptv.Logging
{
    /// <summary>
    /// The logging surface the shared code uses. Each host adapts its own logger to it (Emby's
    /// ILogger has these exact signatures, Jellyfin's Microsoft.Extensions.Logging is wrapped).
    /// Messages use string.Format placeholders ({0}, {1}).
    /// </summary>
    public interface ILog
    {
        void Debug(string message, params object[] paramList);

        void Info(string message, params object[] paramList);

        void Warn(string message, params object[] paramList);

        void Error(string message, params object[] paramList);

        void ErrorException(string message, Exception exception, params object[] paramList);
    }
}
