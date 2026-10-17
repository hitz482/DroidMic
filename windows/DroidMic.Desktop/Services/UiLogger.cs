using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace DroidMic.Desktop.Services;

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string LevelText => Level switch
    {
        LogLevel.Error or LogLevel.Critical => "ERROR",
        LogLevel.Warning => "WARN",
        LogLevel.Debug or LogLevel.Trace => "DEBUG",
        _ => "INFO",
    };
}

/// <summary>
/// Logger that keeps the last entries for the Diagnostics view and appends to
/// %LocalAppData%\DroidMic\logs\droidmic.log (rotated at 1 MB). PINs and keys are
/// never logged by the core; IP addresses are partially masked here.
/// </summary>
public sealed partial class UiLogger : ILogger
{
    private const int MaxEntries = 500;
    private readonly object _fileLock = new();
    private readonly string _path;

    public UiLogger()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DroidMic", "logs");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "droidmic.log");
    }

    public ObservableCollection<LogEntry> Entries { get; } = new();
    public string LogFilePath => _path;
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= MinimumLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        string message = MaskAddresses(formatter(state, exception));
        if (exception is not null && logLevel >= LogLevel.Error) message += " | " + exception.GetType().Name + ": " + exception.Message;
        var entry = new LogEntry(DateTime.Now, logLevel, message);

        lock (_fileLock)
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > 1_000_000) File.Move(_path, _path + ".1", true);
                File.AppendAllText(_path, $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} {entry.LevelText,-5} {entry.Message}{Environment.NewLine}");
            }
            catch (IOException) { /* logging must never break the app */ }
        }

        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            Entries.Add(entry);
            while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
        });
    }

    public void Info(string message) => Log(LogLevel.Information, default, message, null, (s, _) => s);
    public void Warn(string message) => Log(LogLevel.Warning, default, message, null, (s, _) => s);
    public void Error(string message) => Log(LogLevel.Error, default, message, null, (s, _) => s);

    // 192.168.1.23 -> 192.168.x.23 : enough to troubleshoot, less to leak in shared logs.
    private static string MaskAddresses(string s) => Ipv4().Replace(s, m => $"{m.Groups[1].Value}.{m.Groups[2].Value}.x.{m.Groups[4].Value}");

    [GeneratedRegex(@"\b(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\b")]
    private static partial Regex Ipv4();
}
