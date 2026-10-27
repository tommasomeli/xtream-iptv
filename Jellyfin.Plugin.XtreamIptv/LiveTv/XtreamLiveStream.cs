using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XtreamIptv.LiveTv
{
    /// <summary>
    /// One upstream connection per channel, copied to a temp file that every consumer (players,
    /// recordings) reads through Jellyfin's LiveStreamFiles route. Modelled on Jellyfin's own
    /// SharedHttpStream: it keeps a Dispatcharr channel on a single client connection, and the
    /// media source Jellyfin logs after opening carries the local URL, never the provider's.
    /// </summary>
    public class XtreamLiveStream : ILiveStream, IDirectStreamProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServerApplicationHost _appHost;
        private readonly IFileSystem _fileSystem;
        private readonly IStreamHelper _streamHelper;
        private readonly ILogger _logger;
        private readonly string _userAgent;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly string _tempFilePath;

        public XtreamLiveStream(
            MediaSourceInfo mediaSource,
            TunerHostInfo tuner,
            string originalStreamId,
            IFileSystem fileSystem,
            IHttpClientFactory httpClientFactory,
            ILogger logger,
            IConfigurationManager configurationManager,
            IServerApplicationHost appHost,
            IStreamHelper streamHelper,
            string userAgent)
        {
            OriginalMediaSource = mediaSource;
            MediaSource = mediaSource;
            OriginalStreamId = originalStreamId;
            TunerHostId = tuner?.Id;
            _fileSystem = fileSystem;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _appHost = appHost;
            _streamHelper = streamHelper;
            _userAgent = userAgent;
            UniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            ConsumerCount = 1;
            EnableStreamSharing = true;
            _tempFilePath = Path.Combine(configurationManager.GetTranscodePath(), UniqueId + ".ts");
        }

        public MediaSourceInfo OriginalMediaSource { get; }

        public MediaSourceInfo MediaSource { get; set; }

        public int ConsumerCount { get; set; }

        public string OriginalStreamId { get; set; }

        public string TunerHostId { get; }

        public bool EnableStreamSharing { get; private set; }

        public string UniqueId { get; }

        public DateTime DateOpened { get; private set; }

        internal string TempFilePath => _tempFilePath;

        public async Task Open(CancellationToken openCancellationToken)
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(_tempFilePath) ?? throw new InvalidOperationException("Transcode path cannot be a root"));

            _logger.LogInformation("Opening Xtream live stream {StreamId}", OriginalStreamId);

            using var request = new HttpRequestMessage(HttpMethod.Get, OriginalMediaSource.Path);
            if (!string.IsNullOrEmpty(_userAgent))
                request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

            // The response is disposed by the copy task.
            var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, openCancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new HttpRequestException(string.Format(CultureInfo.InvariantCulture, "Upstream answered HTTP {0}", status));
            }

            var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = StartStreaming(response, opened, _cancellation.Token);

            MediaSource.Path = _appHost.GetApiUrlForLocalAccess() + "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
            MediaSource.Protocol = MediaProtocol.Http;

            if (!await opened.Task.ConfigureAwait(false))
            {
                _logger.LogWarning("Zero bytes copied from the Xtream live stream {StreamId}", OriginalStreamId);
                throw new EndOfStreamException("Zero bytes copied from the Xtream live stream");
            }
        }

        private Task StartStreaming(HttpResponseMessage response, TaskCompletionSource<bool> opened, CancellationToken cancellationToken)
        {
            return Task.Run(
                async () =>
                {
                    try
                    {
                        using (response)
                        {
                            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                            await using (stream.ConfigureAwait(false))
                            {
                                var fileStream = new FileStream(_tempFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, IODefaults.FileStreamBufferSize, FileOptions.Asynchronous);
                                await using (fileStream.ConfigureAwait(false))
                                {
                                    await _streamHelper.CopyToAsync(stream, fileStream, IODefaults.CopyToBufferSize, () => Resolve(opened), cancellationToken).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException ex)
                    {
                        _logger.LogInformation("Xtream live stream {StreamId} copy cancelled", OriginalStreamId);
                        opened.TrySetException(ex);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error copying the Xtream live stream {StreamId}", OriginalStreamId);
                        opened.TrySetException(ex);
                    }

                    opened.TrySetResult(false);
                    EnableStreamSharing = false;
                    await DeleteTempFile(_tempFilePath).ConfigureAwait(false);
                },
                CancellationToken.None);
        }

        private void Resolve(TaskCompletionSource<bool> opened)
        {
            DateOpened = DateTime.UtcNow;
            opened.TrySetResult(true);
        }

        public async Task Close()
        {
            EnableStreamSharing = false;
            _logger.LogInformation("Closing Xtream live stream {StreamId}", OriginalStreamId);
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }

        public Stream GetStream()
        {
            var stream = new FileStream(_tempFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, IODefaults.FileStreamBufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
            // A late joiner starts near the live edge instead of replaying the whole file.
            if ((DateTime.UtcNow - DateOpened).TotalSeconds > 10)
                TrySeek(stream, -20000);
            return stream;
        }

        private async Task DeleteTempFile(string path, int retryCount = 0)
        {
            try
            {
                _fileSystem.DeleteFile(path);
            }
            catch (Exception ex)
            {
                if (retryCount > 40)
                {
                    _logger.LogError(ex, "Could not delete the live stream temp file");
                    return;
                }
                await Task.Delay(500).ConfigureAwait(false);
                await DeleteTempFile(path, retryCount + 1).ConfigureAwait(false);
            }
        }

        private void TrySeek(FileStream stream, long offset)
        {
            if (!stream.CanSeek) return;
            try { stream.Seek(offset, SeekOrigin.End); }
            catch (IOException) { }
            catch (ArgumentException) { }
            catch (Exception ex) { _logger.LogError(ex, "Error seeking the live stream file"); }
        }

        public void Dispose()
        {
            _cancellation.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
