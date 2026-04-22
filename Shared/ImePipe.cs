using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace ResoniteBetterIMESupport.Shared;

internal static class ImePipe
{
    public const string PipeName = "ResoniteBetterIMESupport.IME.v1";

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
    readonly Thread _thread;
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
                stream.WaitForConnection();
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (!_disposed && stream.IsConnected)
                {
                    var line = reader.ReadLine();

                    if (line == null)
                        break;

                    _onMessage(ImePipe.DecodeMessage(line));
                }
            }
            catch
            {
                if (!_disposed)
                    Thread.Sleep(500);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
