using System.Collections.Concurrent;
using System.Windows.Threading;
using MuxStudio.Core;

namespace MuxStudio.App;

/// <summary>
/// Chạy MỘT việc nặng ở luồng phụ (giao diện không bao giờ đơ) — port <c>W.Runner</c> của bản Python.
/// <list type="bullet">
/// <item>Log / tiến độ đi qua <see cref="IProgress{T}"/> của <see cref="Job"/>; dòng log được DỊCH ngay ở luồng phụ, rồi gom 80 ms một lần
/// mới đẩy lên giao diện.</item>
/// <item><c>fn(job)</c> chạy ở luồng phụ; <c>onDone(kết quả)</c> / <c>onError(lỗi)</c> chạy ở luồng giao diện.</item>
/// <item>Nút "Dừng sau file này" → <see cref="StopAfterCurrent"/>; "Dừng ngay" → <see cref="CancelNow"/> (diệt cả cây tiến trình con,
/// chạy ở luồng riêng vì có thể chờ tới 3 s).</item>
/// <item><see cref="Ask{T}"/>: lõi (luồng phụ) nhờ luồng giao diện mở hộp thoại (CAPTCHA) rồi chờ kết quả.</item>
/// </list>
/// </summary>
public sealed class Runner
{
    private const int FlushMs = 80;
    private const int MaxPerFlush = 2000;

    private readonly Dispatcher _ui;
    private readonly Action<IReadOnlyList<(string Level, string Message)>> _write;
    private readonly Action<int, int, string> _progress;
    private readonly Action<bool, string> _onBusy;
    private readonly Func<string, string> _redact;
    private readonly ConcurrentQueue<(string, string)> _q = new();
    private readonly DispatcherTimer _timer;
    private readonly List<Waiting> _waiting = [];
    private readonly Lock _progLock = new();
    private JobProgress? _lastProg;

    private sealed record Waiting(string Title, Func<Job, object?> Fn, Action<object?>? OnDone, Action<Exception>? OnError);

    public Runner(Dispatcher ui, Action<IReadOnlyList<(string Level, string Message)>> write, Action<int, int, string> progress,
                  Action<bool, string> onBusy, Func<string, string> redact)
    {
        _ui = ui;
        _write = write;
        _progress = progress;
        _onBusy = onBusy;
        _redact = redact;
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(FlushMs) };
        _timer.Tick += (_, _) =>
        {
            Flush();
            if (Job is null && _q.IsEmpty)
                _timer.Stop();
        };
    }

    public Job? Job { get; private set; }

    public string Title { get; private set; } = "";

    public bool Busy => Job is not null;

    private sealed class LogSink(Runner r) : IProgress<LogLine>
    {
        // luong phu: dich truoc khi day sang giao dien (I18n.Tr ~100 µs/dong)
        public void Report(LogLine value) => r._q.Enqueue((value.Level, I18n.T(value.Message)));
    }

    private sealed class ProgressSink(Runner r) : IProgress<JobProgress>
    {
        public void Report(JobProgress value)
        {
            lock (r._progLock)
                r._lastProg = value;
        }
    }

    /// <summary>Bắt đầu một việc (chỉ một việc mỗi lúc). Đang có việc khác: <paramref name="whenFree"/> → xếp hàng, chạy ngay khi việc đang chạy
    /// xong (việc tự động: đếm file, kiểm tra key); không thì báo và trả <c>false</c>.</summary>
    public bool Start<T>(string title, Func<Job, T> fn, Action<T>? onDone = null, Action<Exception>? onError = null, bool whenFree = false) =>
        StartCore(title, j => fn(j), onDone is null ? null : o => onDone((T)o!), onError, whenFree);

    public bool Start(string title, Action<Job> fn, Action? onDone = null, Action<Exception>? onError = null, bool whenFree = false) =>
        StartCore(title, j =>
        {
            fn(j);
            return null;
        }, onDone is null ? null : _ => onDone(), onError, whenFree);

    private bool StartCore(string title, Func<Job, object?> fn, Action<object?>? onDone, Action<Exception>? onError, bool whenFree)
    {
        if (Busy)
        {
            if (whenFree)
            {
                if (_waiting.All(w => w.Title != title))
                    _waiting.Add(new Waiting(title, fn, onDone, onError));
                return true;
            }
            _write([("warn", L.T($"[!] Đang chạy \"{Title}\" — đợi xong (hoặc bấm Dừng) rồi bấm lại."))]);
            return false;
        }
        var job = new Job(new LogSink(this), new ProgressSink(this), _redact);
        Job = job;
        Title = title;
        lock (_progLock)
            _lastProg = null;
        _onBusy(true, title);
        _timer.Start();
        Task.Run(() =>
        {
            string status;
            object? payload;
            try
            {
                payload = fn(job);
                status = "ok";
            }
            catch (OperationCanceledException)
            {
                (status, payload) = ("cancel", null);
            }
            catch (Exception e)
            {
                (status, payload) = ("error", e);
            }
            _ui.BeginInvoke(DispatcherPriority.Background, () => Finish(job, status, payload, onDone, onError));
        });
        return true;
    }

    /// <summary>Đẩy log + tiến độ đang chờ lên giao diện (luồng giao diện).</summary>
    public void Flush()
    {
        if (!_q.IsEmpty)
        {
            var items = new List<(string, string)>();
            while (items.Count < MaxPerFlush && _q.TryDequeue(out var it))
                items.Add(it);
            _write(items);
        }
        JobProgress? p;
        lock (_progLock)
        {
            p = _lastProg;
            _lastProg = null;
        }
        if (p is { } pv)
            _progress(pv.Done, pv.Total, pv.Label);
    }

    private void Finish(Job job, string status, object? payload, Action<object?>? onDone, Action<Exception>? onError)
    {
        while (!_q.IsEmpty)
            Flush();
        Flush();
        var title = Title;
        Job = null;
        Title = "";
        job.Dispose();
        _onBusy(false, title);
        try
        {
            if (status == "ok")
            {
                onDone?.Invoke(payload);
            }
            else if (status == "cancel")
            {
                _write([("warn", L.T($"[!] Đã dừng ngay: {title}."))]);
                onError?.Invoke(new Cancelled());
            }
            else
            {
                var e = (Exception)payload!;
                var lines = new List<(string, string)> { ("error", L.T($"[LỖI] {title}: {e.Message}")) };
                lines.AddRange(TraceTail(e, 6).Select(l => ("dim", "    " + l)));
                _write(lines);
                onError?.Invoke(e);
            }
        }
        catch (Exception e) // loi trong ham hien thi khong duoc lam chet vong chay
        {
            var lines = new List<(string, string)> { ("error", L.T($"[LỖI giao diện] {e.Message}")) };
            lines.AddRange(TraceTail(e, 6).Select(l => ("dim", "    " + l)));
            _write(lines);
        }
        if (!Busy && _waiting.Count > 0)
        {
            var nxt = _waiting[0];
            _waiting.RemoveAt(0);
            StartCore(nxt.Title, nxt.Fn, nxt.OnDone, nxt.OnError, false);
        }
    }

    public static IEnumerable<string> TraceTail(Exception e, int n) =>
        (e.ToString().Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Trim().Length > 0)).TakeLast(n);

    /// <summary>Gọi từ LUỒNG PHỤ: nhờ luồng giao diện chạy <paramref name="fn"/> (vd mở hộp thoại nhập mã CAPTCHA) rồi chờ lấy kết quả. Bấm
    /// "Dừng ngay" trong lúc chờ → <see cref="Cancelled"/>.</summary>
    public T? Ask<T>(Func<T> fn)
    {
        var job = Job;
        T? value = default;
        Exception? err = null;
        var op = _ui.InvokeAsync(() =>
        {
            try
            {
                value = fn();
            }
            catch (Exception e)
            {
                err = e;
            }
        });
        while (op.Wait(TimeSpan.FromMilliseconds(100)) != DispatcherOperationStatus.Completed)
            job?.Check();
        if (err is not null)
            throw err;
        return value;
    }

    public void StopAfterCurrent()
    {
        if (Job is { } j)
        {
            j.StopAfterCurrent();
            _write([("warn", L.T("[i] Sẽ dừng sau file đang chạy…"))]);
        }
    }

    public void CancelNow()
    {
        var job = Job;
        _waiting.Clear();
        if (job is not null)
        {
            _write([("warn", L.T("[!] Đang dừng ngay (kết thúc tiến trình con)…"))]);
            Task.Run(() =>
            {
                try
                {
                    job.CancelNow();
                }
                catch (ObjectDisposedException)
                {
                    // viec vua xong dung luc bam
                }
            });
        }
    }
}
