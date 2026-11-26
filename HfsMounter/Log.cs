using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HfsMounter;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// 线程安全的日志：始终写文件（按天滚动、自动清理），控制台存在时同时回显。
/// 初始化后会接管 <see cref="Console.Out"/>/<see cref="Console.Error"/>，
/// 这样底层原有代码里的 Console.WriteLine 也会进入日志文件（服务模式无控制台时尤其重要）。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly List<string> Recent = new();
    private const int RecentCapacity = 300;

    private static TextWriter? _consoleOut;
    private static TextWriter? _consoleErr;
    private static StreamWriter? _file;
    private static DateTime _fileDate;
    private static string? _directory;
    private static LogLevel _minLevel = LogLevel.Info;
    private static int _retainDays = 14;
    private static bool _installed;

    public static string? LogDirectory => _directory;

    /// <summary>设置日志输出。directory 为 null 时只输出到控制台。</summary>
    public static void Initialize(string? directory, LogLevel minLevel, int retainDays, bool captureConsole)
    {
        lock (Gate)
        {
            _minLevel = minLevel;
            _retainDays = Math.Max(1, retainDays);
            _directory = directory;
            CloseFileLocked();
            if (directory != null)
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    CleanupOldFilesLocked(directory, _retainDays);
                }
                catch (Exception ex)
                {
                    _directory = null;
                    _consoleOut?.WriteLine($"[日志] 无法创建日志目录 {directory}: {ex.Message}");
                    _directory = null;
                }
            }
        }

        if (!captureConsole || _installed) return;
        _installed = true;
        _consoleOut ??= Console.Out;
        _consoleErr ??= Console.Error;
        Console.SetOut(new TeeWriter(line => Emit(LogLevel.Info, line)));
        Console.SetError(new TeeWriter(line => Emit(LogLevel.Error, line)));
    }

    public static void Debug(string message) => Emit(LogLevel.Debug, message);
    public static void Info(string message) => Emit(LogLevel.Info, message);
    public static void Warn(string message) => Emit(LogLevel.Warn, message);
    public static void Error(string message) => Emit(LogLevel.Error, message);

    public static void Error(string message, Exception ex)
    {
        Emit(LogLevel.Error, message + " :: " + ex.GetType().Name + ": " + ex.Message);
        Emit(LogLevel.Debug, ex.ToString());
    }

    /// <summary>原样输出到控制台：不加时间戳、不重复写文件，用于回显历史日志。</summary>
    public static void Raw(string text)
    {
        try
        {
            TextWriter? target = _consoleOut;
            if (target != null) target.WriteLine(text);
            else Console.WriteLine(text);
        }
        catch (Exception)
        {
            // 控制台已关闭
        }
    }

    /// <summary>读取最新日志文件的末尾若干行（跨进程，能反映服务进程写过什么）。</summary>
    public static IReadOnlyList<string> TailFile(int maxLines)
    {
        var lines = new List<string>();
        string? directory = _directory;
        if (directory == null || !Directory.Exists(directory)) return lines;

        FileInfo? newest = null;
        try
        {
            foreach (FileInfo file in new DirectoryInfo(directory).GetFiles("hfsmounter-*.log"))
            {
                if (newest == null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = file;
            }
        }
        catch (Exception)
        {
            return lines;
        }
        if (newest == null) return lines;

        try
        {
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                buffer.Enqueue(line);
                if (buffer.Count > maxLines) buffer.Dequeue();
            }
            lines.AddRange(buffer);
        }
        catch (Exception)
        {
            // 日志文件正在被写入或已删除，忽略
        }
        return lines;
    }

    /// <summary>最近若干条日志（本进程内存里的，供调试使用）。</summary>
    public static IReadOnlyList<string> RecentLines(int count)
    {
        lock (Gate)
        {
            int start = Math.Max(0, Recent.Count - count);
            return Recent.GetRange(start, Recent.Count - start);
        }
    }

    private static void Emit(LogLevel level, string message)
    {
        if (level < _minLevel && level != LogLevel.Error) return;

        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        char tag = level switch
        {
            LogLevel.Debug => 'D',
            LogLevel.Info => 'I',
            LogLevel.Warn => 'W',
            _ => 'E',
        };
        string line = $"{stamp} [{tag}] {message}";

        lock (Gate)
        {
            Recent.Add(line);
            if (Recent.Count > RecentCapacity) Recent.RemoveRange(0, Recent.Count - RecentCapacity);

            TextWriter? console = level == LogLevel.Error ? _consoleErr : _consoleOut;
            if (console != null)
            {
                try { console.WriteLine(line); }
                catch (IOException) { /* 无控制台（服务模式） */ }
                catch (ObjectDisposedException) { }
            }

            if (_directory == null) return;
            try
            {
                StreamWriter file = EnsureFileLocked();
                file.WriteLine(line);
            }
            catch (IOException)
            {
                // 磁盘故障时不能因为日志而崩溃
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static StreamWriter EnsureFileLocked()
    {
        DateTime today = DateTime.Now.Date;
        if (_file != null && _fileDate == today) return _file;

        CloseFileLocked();
        string path = Path.Combine(_directory!, $"hfsmounter-{today:yyyyMMdd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _fileDate = today;
        return _file;
    }

    private static void CloseFileLocked()
    {
        try { _file?.Dispose(); } catch { }
        _file = null;
    }

    private static void CleanupOldFilesLocked(string directory, int retainDays)
    {
        DateTime cutoff = DateTime.Now.Date.AddDays(-retainDays);
        foreach (string file in Directory.EnumerateFiles(directory, "hfsmounter-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>把任意 Write/WriteLine 拆成整行后交给日志。</summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly Action<string> _sink;
        private readonly StringBuilder _pending = new();

        public TeeWriter(Action<string> sink) => _sink = sink;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Append(value.ToString());

        public override void Write(string? value)
        {
            if (!string.IsNullOrEmpty(value)) Append(value);
        }

        public override void WriteLine() => Append("\n");

        public override void WriteLine(string? value)
        {
            if (!string.IsNullOrEmpty(value)) Append(value);
            Append("\n");
        }

        private void Append(string text)
        {
            _pending.Append(text);
            while (true)
            {
                string buffer = _pending.ToString();
                int newline = buffer.IndexOf('\n');
                if (newline < 0) break;
                string line = buffer.Substring(0, newline).TrimEnd('\r');
                _pending.Clear();
                _pending.Append(buffer, newline + 1, buffer.Length - newline - 1);
                if (line.Length > 0) _sink(line);
            }
        }

        public override void Flush()
        {
            if (_pending.Length == 0) return;
            string line = _pending.ToString().TrimEnd('\r', '\n');
            _pending.Clear();
            if (line.Length > 0) _sink(line);
        }
    }
}
