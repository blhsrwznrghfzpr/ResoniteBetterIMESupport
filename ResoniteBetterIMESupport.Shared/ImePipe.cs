using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace ResoniteBetterIMESupport.Shared;

internal static class ImePipe
{
    const string BasePipeName = "ResoniteBetterIMESupport.IME.v1";
    static readonly Lazy<string> PipeNameValue = new(BuildPipeName);

    public static string PipeName => PipeNameValue.Value;

    public static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    public static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));

    public static string EncodeMessage(string composition, string committedText, int caretOffset) => $"{Encode(composition)}\t{Encode(committedText)}\t{caretOffset}";

    public static ImePipeMessage DecodeMessage(string line)
    {
        var parts = line.Split('\t');

        return new ImePipeMessage(
            parts.Length > 0 ? Decode(parts[0]) : string.Empty,
            parts.Length > 1 ? Decode(parts[1]) : string.Empty,
            parts.Length > 2 && int.TryParse(parts[2], out var caretOffset) ? caretOffset : -1);
    }

    public static bool TryDecodeMessage(string line, out ImePipeMessage message)
    {
        try
        {
            message = DecodeMessage(line);
            return true;
        }
        catch
        {
            message = default;
            return false;
        }
    }

    static string BuildPipeName()
    {
        var sessionId = GetResoniteSessionId();

        if (string.IsNullOrWhiteSpace(sessionId))
            return BasePipeName;

        return $"{BasePipeName}.{SanitizePipeNamePart(sessionId)}";
    }

    static string GetResoniteSessionId()
    {
        var args = Environment.GetCommandLineArgs();

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("-shmprefix", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];

            if (!args[i].Equals("-QueueName", StringComparison.OrdinalIgnoreCase))
                continue;

            var queueName = args[i + 1];
            var separatorIndex = queueName.IndexOf('_');
            return separatorIndex <= 0 ? queueName : queueName.Substring(0, separatorIndex);
        }

        return string.Empty;
    }

    static string SanitizePipeNamePart(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) || character == '.' || character == '-' || character == '_')
                builder.Append(character);
            else
                builder.Append('_');
        }

        return builder.ToString();
    }
}

internal readonly struct ImePipeMessage
{
    public ImePipeMessage(string composition, string committedText, int caretOffset)
    {
        Composition = composition;
        CommittedText = committedText;
        CaretOffset = caretOffset;
    }

    public string Composition { get; }

    public string CommittedText { get; }

    public int CaretOffset { get; }
}

internal sealed class ImePipeClient : IDisposable
{
    readonly object _lock = new();
    NamedPipeClientStream? _stream;
    StreamWriter? _writer;
    DateTime _nextConnectAttemptUtc;

    public bool SendComposition(string composition, string committedText, int caretOffset)
    {
        lock (_lock)
        {
            if (!EnsureConnected())
                return false;

            try
            {
                _writer!.WriteLine(ImePipe.EncodeMessage(composition, committedText, caretOffset));
                return true;
            }
            catch
            {
                Disconnect();
                return false;
            }
        }
    }

    bool EnsureConnected()
    {
        if (_stream?.IsConnected == true && _writer != null)
            return true;

        if (DateTime.UtcNow < _nextConnectAttemptUtc)
            return false;

        _nextConnectAttemptUtc = DateTime.UtcNow.AddSeconds(1);
        Disconnect();

        try
        {
            var stream = new NamedPipeClientStream(".", ImePipe.PipeName, PipeDirection.Out);
            stream.Connect(10);
            _stream = stream;
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            return true;
        }
        catch
        {
            Disconnect();
            return false;
        }
    }

    void Disconnect()
    {
        try
        {
            _writer?.Dispose();
        }
        catch
        {
        }

        try
        {
            _stream?.Dispose();
        }
        catch
        {
        }

        _writer = null;
        _stream = null;
    }

    public void Dispose()
    {
        lock (_lock)
            Disconnect();
    }
}

internal sealed class ImePipeServer : IDisposable
{
    readonly Action<ImePipeMessage> _onMessage;
    readonly object _lock = new();
    readonly Thread _thread;
    NamedPipeServerStream? _stream;
    volatile bool _disposed;

    public ImePipeServer(Action<ImePipeMessage> onMessage)
    {
        _onMessage = onMessage;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ResoniteBetterIMESupport IME pipe"
        };
        _thread.Start();
    }

    void Run()
    {
        while (!_disposed)
        {
            try
            {
                using var stream = new NamedPipeServerStream(ImePipe.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                lock (_lock)
                    _stream = stream;

                stream.WaitForConnection();
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (!_disposed && stream.IsConnected)
                {
                    var line = reader.ReadLine();

                    if (line == null)
                        break;

                    if (ImePipe.TryDecodeMessage(line, out var message))
                        _onMessage(message);
                }
            }
            catch
            {
                if (!_disposed)
                    Thread.Sleep(500);
            }
            finally
            {
                lock (_lock)
                    _stream = null;
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;

        lock (_lock)
            _stream?.Dispose();
    }
}
