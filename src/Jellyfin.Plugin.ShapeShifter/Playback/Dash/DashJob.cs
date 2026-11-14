using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.Playback.Dash;

/// <summary>
/// One DASH session: an ffmpeg process writing fMP4 segments into a run directory. Segment
/// requests outside what the current run can deliver soon restart ffmpeg at that segment.
/// Old segments are deleted as playback moves on, and ffmpeg is paused when it gets far
/// ahead, so disk use stays bounded like a real transcoder's.
/// </summary>
public sealed class DashJob : IAsyncDisposable
{
    private const int RestartAheadThreshold = 6;
    private const int KeepBehind = 8;
    private const int PauseAhead = 45;
    private const int ResumeAhead = 20;
    private static readonly TimeSpan SegmentTimeout = TimeSpan.FromSeconds(45);

    private readonly string _ffmpegPath;
    private readonly string _rootDir;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Process? _process;
    private string _runDir = string.Empty;
    private int _runStart;
    private int _runNumber;
    private int _lastRequested;
    private int _availableFrom;
    private bool _paused;
    private bool _fellBack;
    private readonly object _patchLock = new();
    private readonly HashSet<string> _patched = [];
    private readonly Dictionary<string, (uint Timescale, long Delta)> _runShift = [];

    public DashJob(DashOptions options, string ffmpegPath, string rootDir, ILogger logger)
    {
        Options = options;
        _ffmpegPath = ffmpegPath;
        _rootDir = rootDir;
        _logger = logger;
        Directory.CreateDirectory(rootDir);
    }

    public DashOptions Options { get; private set; }

    public DateTime LastAccess { get; private set; } = DateTime.UtcNow;

    public Task StartAsync() => RestartAsync(0);

    /// <summary>Path of the init segment for a representation, once written.</summary>
    public async Task<string?> GetHeaderAsync(string rep, CancellationToken ct)
    {
        LastAccess = DateTime.UtcNow;
        var deadline = DateTime.UtcNow + SegmentTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var path = Path.Combine(_runDir, $"init-{rep}.m4s");
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return path;
            }

            if (await HandleExitAsync().ConfigureAwait(false))
            {
                return null;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Path of media segment <paramref name="number"/> (0-based, presentation-wide), once complete.</summary>
    public async Task<string?> GetSegmentAsync(string rep, int number, CancellationToken ct)
    {
        LastAccess = DateTime.UtcNow;
        if (number < 0 || number >= Options.SegmentCount)
        {
            return null;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (rep == DashManifest.VideoRepresentation)
            {
                _lastRequested = number;
            }

            var produced = ProducedUpTo(DashManifest.VideoRepresentation);
            if (number < _availableFrom || number > Math.Max(produced, _runStart) + RestartAheadThreshold)
            {
                _logger.LogDebug("DASH seek to segment {Number} (run covers {Start}-{Produced}); restarting", number, _runStart, produced);
                await RestartAsync(number).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }

        var deadline = DateTime.UtcNow + SegmentTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var path = SegmentPath(rep, number);
            var exited = _process is null || _process.HasExited;

            // A segment is complete once the next one has started, or ffmpeg has finished.
            if (File.Exists(path) && (exited || File.Exists(SegmentPath(rep, number + 1))))
            {
                Housekeep(number);
                return Place(rep, path);
            }

            if (exited && await HandleExitAsync().ConfigureAwait(false))
            {
                return File.Exists(path) ? Place(rep, path) : null;
            }

            Housekeep(number);
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        _logger.LogWarning("Timed out waiting for DASH segment {Rep}/{Number}", rep, number);
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            Kill();
            TryDelete(_rootDir);
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }

    /// <summary>
    /// Moves a finished segment to its place in the presentation timeline (once). ffmpeg starts
    /// every run's decode times at zero, so a run started at segment N is shifted by N segments,
    /// measured against the run's own first segment so any start offset ffmpeg did keep is
    /// accounted for. Init segments carry no edit list, so they're identical for every run.
    /// </summary>
    private string? Place(string rep, string path)
    {
        lock (_patchLock)
        {
            if (_patched.Contains(path))
            {
                return path;
            }

            try
            {
                if (!_runShift.TryGetValue(rep, out var shift))
                {
                    var init = Path.Combine(_runDir, $"init-{rep}.m4s");
                    var timescale = File.Exists(init) ? Fmp4Timestamps.ReadTimescale(File.ReadAllBytes(init)) : null;
                    if (timescale is not { } ts)
                    {
                        _logger.LogWarning("DASH: no timescale in init segment for representation {Rep}; serving segment unshifted", rep);
                        return path;
                    }

                    var first = SegmentPath(rep, _runStart);
                    var firstTime = File.Exists(first) ? Fmp4Timestamps.ReadDecodeTime(File.ReadAllBytes(first)) ?? 0 : 0;
                    var expected = (long)_runStart * DashOptions.SegmentSeconds * ts;
                    shift = (ts, expected - (long)firstTime);
                    _runShift[rep] = shift;
                }

                if (shift.Delta != 0)
                {
                    var bytes = File.ReadAllBytes(path);
                    if (!Fmp4Timestamps.Shift(bytes, shift.Delta, shift.Timescale))
                    {
                        _logger.LogWarning("DASH: couldn't shift timestamps of {Path}", path);
                        return path;
                    }

                    var temp = Path.Combine(Path.GetDirectoryName(path)!, "shift-" + Path.GetFileName(path) + ".part");
                    File.WriteAllBytes(temp, bytes);
                    File.Move(temp, path, overwrite: true);
                }

                _patched.Add(path);
                return path;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "DASH: couldn't place segment {Path}", path);
                return File.Exists(path) ? path : null;
            }
        }
    }

    private string SegmentPath(string rep, int number) =>
        Path.Combine(_runDir, string.Create(CultureInfo.InvariantCulture, $"chunk-{rep}-{number - _runStart + 1:00000}.m4s"));

    /// <summary>Highest presentation segment number whose file exists in the current run.</summary>
    private int ProducedUpTo(string rep)
    {
        if (!Directory.Exists(_runDir))
        {
            return _runStart - 1;
        }

        var max = 0;
        foreach (var file in Directory.EnumerateFiles(_runDir, $"chunk-{rep}-*.m4s"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(name[(name.LastIndexOf('-') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                max = Math.Max(max, n);
            }
        }

        return _runStart + max - 1;
    }

    private Task RestartAsync(int startSegment)
    {
        Kill();
        if (!string.IsNullOrEmpty(_runDir))
        {
            TryDelete(_runDir);
        }

        _runStart = startSegment;
        lock (_patchLock)
        {
            _runShift.Clear();
            _patched.Clear();
        }

        _availableFrom = startSegment;
        _lastRequested = startSegment;
        _runDir = Path.Combine(_rootDir, "run" + (++_runNumber).ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_runDir);

        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in DashCommandBuilder.Build(Options, startSegment, _runDir))
        {
            psi.ArgumentList.Add(arg);
        }

        _logger.LogDebug("Starting DASH ffmpeg: {Args}", string.Join(' ', psi.ArgumentList));
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogDebug("ffmpeg: {Line}", e.Data);
            }
        };
        process.OutputDataReceived += (_, _) => { };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        _process = process;
        _paused = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deals with ffmpeg having exited. A hardware encoder that fails before producing anything
    /// is retried once in software. Returns true when nothing more will be produced.
    /// </summary>
    private async Task<bool> HandleExitAsync()
    {
        var process = _process;
        if (process is null || !process.HasExited)
        {
            return false;
        }

        if (process.ExitCode != 0 && !_fellBack && Options.Encoder != "libx264" && ProducedUpTo(DashManifest.VideoRepresentation) < _runStart)
        {
            _logger.LogWarning("DASH encoder {Encoder} failed (exit {Code}); falling back to libx264", Options.Encoder, process.ExitCode);
            _fellBack = true;
            Options = Options with { Encoder = "libx264", VaapiDevice = null };
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                await RestartAsync(_runStart).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }

            return false;
        }

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("DASH ffmpeg exited with code {Code}", process.ExitCode);
        }

        return true;
    }

    /// <summary>Drops segments well behind playback and pauses ffmpeg when it's far ahead.</summary>
    private void Housekeep(int current)
    {
        var dropBelow = current - KeepBehind;
        _availableFrom = Math.Max(_availableFrom, dropBelow);
        foreach (var file in Directory.Exists(_runDir) ? Directory.EnumerateFiles(_runDir, "chunk-*.m4s") : [])
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(name[(name.LastIndexOf('-') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                && _runStart + n - 1 < dropBelow)
            {
                TryDeleteFile(file);
            }
        }

        var ahead = ProducedUpTo(DashManifest.VideoRepresentation) - Math.Max(current, _lastRequested);
        if (!_paused && ahead > PauseAhead)
        {
            _paused = Signal(SigStop);
        }
        else if (_paused && ahead < ResumeAhead)
        {
            _paused = !Signal(SigCont);
        }
    }

    private void Kill()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (_paused)
            {
                Signal(SigCont, process);
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        finally
        {
            process.Dispose();
        }
    }

    private const int SigStop = 19;
    private const int SigCont = 18;

    private bool Signal(int linuxSignal, Process? process = null)
    {
        process ??= _process;
        if (process is null || process.HasExited || OperatingSystem.IsWindows())
        {
            return false;
        }

        // SIGSTOP/SIGCONT numbers differ on macOS/BSD.
        var signal = OperatingSystem.IsLinux() ? linuxSignal : linuxSignal == SigStop ? 17 : 19;
        return NativeKill(process.Id, signal) == 0;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int NativeKill(int pid, int sig);

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
    }
}
