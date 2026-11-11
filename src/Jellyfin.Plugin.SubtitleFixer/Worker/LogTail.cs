using System.Text;

namespace Jellyfin.Plugin.SubtitleFixer.Worker;

/// <summary>
/// Follows a log file another process appends to, and hands each new complete line to a callback. Starts at the file's
/// current end, so only lines written after it starts count. Never locks the file and never throws: a missing or busy
/// file just means no lines this time.
/// </summary>
public sealed class LogTail : IDisposable
{
    private const int MaxReadBytes = 1024 * 1024;

    private readonly string _path;
    private readonly Action<string> _onLine;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _partial = new();
    private readonly object _gate = new();
    private readonly Timer _timer;
    private long _position;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogTail"/> class and starts polling.
    /// </summary>
    /// <param name="path">Log file.</param>
    /// <param name="onLine">Called with each new line, without its line ending.</param>
    /// <param name="interval">How often to look for new lines.</param>
    public LogTail(string path, Action<string> onLine, TimeSpan interval)
    {
        _path = path;
        _onLine = onLine;
        _position = Length(path);
        _timer = new Timer(_ => Poll(), null, interval, interval);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _timer.Dispose();
    }

    /// <summary>Reads what was appended since the last poll. Called by the timer; internal for tests.</summary>
    internal void Poll()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < _position)
                {
                    // The log was replaced; start over at its beginning.
                    _position = 0;
                    _partial.Clear();
                }

                if (stream.Length == _position)
                {
                    return;
                }

                stream.Seek(_position, SeekOrigin.Begin);
                var buffer = new byte[(int)Math.Min(MaxReadBytes, stream.Length - _position)];
                var read = stream.Read(buffer, 0, buffer.Length);
                _position += read;
                var chars = new char[_decoder.GetCharCount(buffer, 0, read)];
                _decoder.GetChars(buffer, 0, read, chars, 0);
                _partial.Append(chars);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            var text = _partial.ToString();
            var end = text.LastIndexOf('\n');
            if (end < 0)
            {
                return;
            }

            _partial.Clear().Append(text, end + 1, text.Length - end - 1);
            foreach (var line in text[..end].Split('\n'))
            {
                try
                {
                    _onLine(line.TrimEnd('\r'));
                }
#pragma warning disable CA1031 // A progress callback must never stop the tail.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        }
    }

    private static long Length(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
