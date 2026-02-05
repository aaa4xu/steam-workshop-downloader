using System;
using System.IO;
using System.Text;

/// <summary>
/// Routes console output to stdout/stderr and optionally a log file.
/// This keeps container execution clean while still allowing persistent logs when requested.
/// </summary>
internal sealed class LogRouter : IDisposable
{
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;
    private readonly FileStream? _logStream;
    private readonly StreamWriter? _logWriter;
    private bool _disposed;

    private LogRouter(TextWriter originalOut, TextWriter originalErr, FileStream? logStream, StreamWriter? logWriter)
    {
        _originalOut = originalOut;
        _originalErr = originalErr;
        _logStream = logStream;
        _logWriter = logWriter;
    }

    public static LogRouter Attach(string? logPath)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;

        if (string.IsNullOrWhiteSpace(logPath))
        {
            return new LogRouter(originalOut, originalErr, null, null);
        }

        var logFullPath = Path.GetFullPath(logPath);
        var logDir = Path.GetDirectoryName(logFullPath);
        if (!string.IsNullOrEmpty(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        var logStream = new FileStream(logFullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        var logWriter = new StreamWriter(logStream) { AutoFlush = true };

        Console.SetOut(new TeeTextWriter(originalOut, logWriter));
        Console.SetError(new TeeTextWriter(originalErr, logWriter));

        return new LogRouter(originalOut, originalErr, logStream, logWriter);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
        _logWriter?.Dispose();
        _logStream?.Dispose();
    }
}

/// <summary>
/// TextWriter that mirrors writes to two underlying writers.
/// </summary>
internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _left;
    private readonly TextWriter _right;

    public TeeTextWriter(TextWriter left, TextWriter right)
    {
        _left = left;
        _right = right;
    }

    public override Encoding Encoding => _left.Encoding;

    public override void Write(char value)
    {
        _left.Write(value);
        _right.Write(value);
    }

    public override void WriteLine(string? value)
    {
        _left.WriteLine(value);
        _right.WriteLine(value);
    }

    public override void Flush()
    {
        _left.Flush();
        _right.Flush();
    }
}
