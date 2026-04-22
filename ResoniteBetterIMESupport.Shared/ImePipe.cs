using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ResoniteBetterIMESupport.Shared;

internal static class ImePipe
{
    const string BasePipeName = "ResoniteBetterIMESupport.IME.v1";
    static readonly Lazy<PipeIdentity> PipeIdentityValue = new(BuildPipeIdentity);

    public static string PipeName => PipeIdentityValue.Value.Name;

    public static string PipeDebugInfo => PipeIdentityValue.Value.DebugInfo;

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

    static PipeIdentity BuildPipeIdentity()
    {
        var process = Process.GetCurrentProcess();
        var parentProcessId = TryGetParentProcessId(process.Id, out var parentId) ? parentId : -1;
        var sessionProcessId = IsRendererProcess(process.ProcessName) && parentProcessId > 0 ? parentProcessId : process.Id;
        var pipeName = $"{BasePipeName}.{sessionProcessId}";
        var source = sessionProcessId == process.Id ? "current-process" : "parent-process";
        var debugInfo = $"pipeName=\"{pipeName}\", source={source}, processName=\"{process.ProcessName}\", processId={process.Id}, parentProcessId={parentProcessId}";
        return new PipeIdentity(pipeName, debugInfo);
    }

    static bool IsRendererProcess(string processName) =>
        processName.IndexOf("Renderer", StringComparison.OrdinalIgnoreCase) >= 0;

    static bool TryGetParentProcessId(int processId, out int parentProcessId)
    {
        parentProcessId = -1;
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);

        if (snapshot == INVALID_HANDLE_VALUE)
            return false;

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };

            if (!Process32First(snapshot, ref entry))
                return false;

            do
            {
                if (entry.th32ProcessID != processId)
                    continue;

                parentProcessId = entry.th32ParentProcessID;
                return true;
            }
            while (Process32Next(snapshot, ref entry));

            return false;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    readonly struct PipeIdentity
    {
        public PipeIdentity(string name, string debugInfo)
        {
            Name = name;
            DebugInfo = debugInfo;
        }

        public string Name { get; }

        public string DebugInfo { get; }
    }

    const uint TH32CS_SNAPPROCESS = 0x00000002;
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public int th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public int th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
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
