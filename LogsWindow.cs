using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClaudeStatusBridge;

/// <summary>
/// Read-only logs window that tails the bridge's per-platform log file
/// (Log.FilePath). Polls every 500ms for new content; cheap enough at
/// the bridge's log volume that we don't need a FileSystemWatcher.
/// </summary>
internal sealed class LogsWindow : Window
{
    private readonly TextBox _textBox;
    private readonly DispatcherTimer _timer;
    private long _lastReadPosition;

    public LogsWindow()
    {
        Title = "ClaudePanel Bridge — Logs";
        Width = 900;
        Height = 500;
        CanResize = true;

        _textBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _textBox,
        };

        // Initial read of whatever is already in the file.
        Refresh(initial: true);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh(initial: false);
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh(bool initial)
    {
        var path = Log.FilePath;
        if (path is null || !File.Exists(path)) return;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (initial || fs.Length < _lastReadPosition)
            {
                fs.Seek(0, SeekOrigin.Begin);
                _lastReadPosition = 0;
            }
            else
            {
                fs.Seek(_lastReadPosition, SeekOrigin.Begin);
            }
            using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
            var added = sr.ReadToEnd();
            _lastReadPosition = fs.Position;
            if (added.Length == 0) return;
            _textBox.Text = (initial ? "" : _textBox.Text ?? "") + added;
            _textBox.CaretIndex = _textBox.Text?.Length ?? 0;
        }
        catch
        {
            // Logs window is best-effort; never crash on a bad read.
        }
    }
}
