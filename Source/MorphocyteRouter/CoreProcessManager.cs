using System.Diagnostics;
using System.IO;

namespace MorphocyteRouter;

/// <summary>Owns the core until exit is confirmed, including failed and timed-out stops.</summary>
public sealed class CoreProcessManager
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<Process, Task> _terminate;
    private readonly TimeSpan _stopTimeout;
    private Process? _process;
    private ProcessLifetimeJob? _processLifetimeJob;
    private Task? _pendingStop;
    private string? _startupFailure;
    private readonly object _failureStateGate = new();
    private bool _startupVerificationComplete;
    private bool _lateFailureQueued;
    public event Action<string>? Output;
    public event Action? Exited;
    public event Action<string>? StartupFailed;
    public bool HasTrackedProcess => _process is not null;
    public int? TrackedProcessId => _process?.Id;
    public bool IsRunning
    {
        get
        {
            var process = _process;
            if (process is null) return false;
            try { return !process.HasExited; }
            catch { return true; } // Unknown is never presented as a confirmed stop.
        }
    }

    public CoreProcessManager(Func<Process, Task>? terminate = null, TimeSpan? stopTimeout = null)
    {
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(4);
        _terminate = terminate ?? (process => Task.Run(() =>
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }));
    }

    public async Task StartAsync(ProcessStartInfo info, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ReapExited();
            if (_process is not null) throw new InvalidOperationException("Предыдущее ядро ещё не завершено. Сначала останови его.");
            cancellationToken.ThrowIfCancellationRequested();
            lock (_failureStateGate)
            {
                _startupFailure = null;
                _startupVerificationComplete = false;
                _lateFailureQueued = false;
            }
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += HandleOutput;
            process.ErrorDataReceived += HandleOutput;
            process.Exited += HandleExit;
            try
            {
                if (!process.Start()) throw new IOException("Не удалось запустить ядро.");
            }
            catch { process.Dispose(); throw; }
            _process = process;
            try
            {
                _processLifetimeJob = ProcessLifetimeJob.Attach(process);
            }
            catch (Exception attachError)
            {
                _pendingStop ??= _terminate(process);
                try
                {
                    await _pendingStop.WaitAsync(_stopTimeout);
                    ReapExited();
                }
                catch (Exception stopError)
                {
                    Output?.Invoke("Windows не смог привязать Mihomo к жизненному циклу приложения, а завершение процесса пока не подтверждено: " + stopError.Message);
                    _pendingStop = null;
                }
                throw new IOException("Не удалось включить безопасную очистку ядра при аварийном закрытии приложения. Mihomo не оставлен работать намеренно; проверь статус ядра и журнал.", attachError);
            }
            if (info.RedirectStandardOutput) process.BeginOutputReadLine();
            if (info.RedirectStandardError) process.BeginErrorReadLine();
            await Task.Delay(TimeSpan.FromMilliseconds(1400), cancellationToken);
            if (process.HasExited)
            {
                var exitCode = process.ExitCode;
                ReapExited();
                throw new IOException($"Ядро завершилось при запуске (код {exitCode}). Проверь журнал.");
            }
            string? startupFailure;
            lock (_failureStateGate)
            {
                startupFailure = _startupFailure;
                _startupVerificationComplete = startupFailure is null;
            }
            if (startupFailure is { } failure)
            {
                _pendingStop ??= _terminate(process);
                try { await _pendingStop.WaitAsync(_stopTimeout); }
                catch (TimeoutException)
                {
                    Output?.Invoke("Ядро сообщило об ошибке запуска, но его остановка пока не подтверждена.");
                    throw new IOException("Mihomo сообщил, что не смог поднять порт или TUN, и не остановился вовремя. Сначала останови его через кнопку «Остановить VPN», затем проверь журнал: " + failure);
                }
                catch (Exception stopError)
                {
                    _pendingStop = null;
                    throw new IOException("Mihomo сообщил об ошибке запуска, а остановить его автоматически не удалось. Проверь журнал и останови ядро вручную: " + failure, stopError);
                }
                ReapExited();
                throw new IOException("Mihomo не поднял сетевые интерфейсы. Возможно, уже запущен другой экземпляр ядра; проверь процессы mihomo.exe и журналы. Детали: " + failure);
            }
        }
        catch (OperationCanceledException)
        {
            // If the app is closing during the startup verification window, do not orphan
            // the process that was just started.
            var process = _process;
            if (process is not null && IsRunning)
            {
                _pendingStop ??= _terminate(process);
                try { await _pendingStop.WaitAsync(_stopTimeout); ReapExited(); }
                catch { /* Keep ownership so a later StopAsync can retry. */ }
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            ReapExited();
            var process = _process;
            if (process is null) return true;
            _pendingStop ??= _terminate(process);
            try { await _pendingStop.WaitAsync(_stopTimeout); }
            catch (TimeoutException)
            {
                Output?.Invoke("Остановка ещё не подтверждена: Windows задерживает завершение ядра.");
                return false;
            }
            catch (Exception ex)
            {
                Output?.Invoke("Не удалось остановить ядро: " + ex.Message);
                _pendingStop = null; // Permit a later retry while retaining process ownership.
                ReapExited();
                return _process is null;
            }
            ReapExited();
            return _process is null;
        }
        finally { _gate.Release(); }
    }

    private void ReapExited()
    {
        if (_process is null || IsRunning || _pendingStop is { IsCompleted: false }) return;
        var process = _process;
        _process = null;
        _pendingStop = null;
        process.OutputDataReceived -= HandleOutput;
        process.ErrorDataReceived -= HandleOutput;
        process.Exited -= HandleExit;
        process.Dispose();
        _processLifetimeJob?.Dispose();
        _processLifetimeJob = null;
    }

    private void HandleOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        if (IsNetworkStartupFailure(e.Data) && sender is Process process)
        {
            var handleLateFailure = false;
            lock (_failureStateGate)
            {
                if (ReferenceEquals(process, _process))
                {
                    _startupFailure ??= e.Data;
                    if (_startupVerificationComplete && !_lateFailureQueued)
                    {
                        _lateFailureQueued = true;
                        handleLateFailure = true;
                    }
                }
            }
            if (handleLateFailure) _ = HandleLateStartupFailureAsync(process, e.Data);
        }
        Output?.Invoke(e.Data);
    }

    private async Task HandleLateStartupFailureAsync(Process process, string failure)
    {
        await _gate.WaitAsync();
        try
        {
            // Stop only the failing child that this manager started. A previous
            // child's late output must never terminate a replacement instance.
            if (!ReferenceEquals(process, _process)) return;
            ReapExited();
            if (_process is null) return;
            var message = "Mihomo сообщил об ошибке сетевого интерфейса после запуска: " + failure;
            try
            {
                _pendingStop ??= _terminate(process);
                await _pendingStop.WaitAsync(_stopTimeout);
                ReapExited();
                message += _process is null ? " Неисправный процесс остановлен." : " Остановка пока не подтверждена.";
            }
            catch (TimeoutException)
            {
                message += " Остановка пока не подтверждена; повтори её кнопкой «Остановить VPN».";
            }
            catch (Exception ex)
            {
                _pendingStop = null;
                ReapExited();
                message += " Не удалось подтвердить остановку: " + ex.Message;
            }
            Output?.Invoke(message);
            StartupFailed?.Invoke(message);
        }
        finally { _gate.Release(); }
    }

    internal static bool IsNetworkStartupFailure(string line)
    {
        var text = line.ToLowerInvariant();
        return (text.Contains("start mixed(") && text.Contains("server error"))
            || (text.Contains("start http server error"))
            || (text.Contains("start socks server error"))
            || text.Contains("start tun listening error")
            || (text.Contains("configure tun interface:") && (text.Contains("error") || text.Contains("cannot create") || text.Contains("already exists")))
            || (text.Contains("listen tcp") && (text.Contains("address already in use") || text.Contains("only one usage of each socket address")));
    }

    public static IReadOnlyList<(int ProcessId, string? ExecutablePath)> FindOtherInstances(string executablePath)
    {
        var processName = Path.GetFileNameWithoutExtension(Path.GetFullPath(executablePath));
        var matches = new List<(int, string?)>();
        foreach (var candidate in Process.GetProcessesByName(processName))
        {
            using (candidate)
            {
                if (candidate.Id == Environment.ProcessId) continue;
                try
                {
                    var actualPath = candidate.MainModule?.FileName;
                    if (actualPath is not null)
                        matches.Add((candidate.Id, Path.GetFullPath(actualPath)));
                }
                catch { matches.Add((candidate.Id, null)); }
            }
        }
        return matches.OrderBy(instance => instance.Item1).ToArray();
    }

    public static async Task StopOtherInstancesAsync(string executablePath,
        IEnumerable<(int ProcessId, string? ExecutablePath)> instances, CancellationToken cancellationToken = default)
    {
        var expectedName = Path.GetFileNameWithoutExtension(Path.GetFullPath(executablePath));
        foreach (var instance in instances.DistinctBy(instance => instance.ProcessId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Process candidate;
            try { candidate = Process.GetProcessById(instance.ProcessId); }
            catch (ArgumentException) { continue; }
            using (candidate)
            {
                string? actualPath;
                if (string.IsNullOrWhiteSpace(instance.ExecutablePath))
                    throw new IOException($"Путь Mihomo PID {instance.ProcessId} недоступен. Для безопасности процесс не остановлен и запуск нового ядра отменён.");
                try { actualPath = candidate.MainModule?.FileName; }
                catch (Exception ex) { throw new IOException($"Не удалось повторно проверить путь Mihomo PID {instance.ProcessId}; процесс не остановлен.", ex); }
                if (actualPath is null || !string.Equals(Path.GetFullPath(actualPath), instance.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Path.GetFileNameWithoutExtension(actualPath), expectedName, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Процесс PID {instance.ProcessId} изменился после проверки; для безопасности он не был остановлен. Повтори запуск ядра.");
                if (candidate.HasExited) continue;
                candidate.Kill(entireProcessTree: true);
                try { await candidate.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(6), cancellationToken); }
                catch (TimeoutException ex) { throw new IOException($"Mihomo PID {instance.ProcessId} не завершился в течение 6 секунд и мог оставить TUN включённым.", ex); }
            }
        }
    }
    private void HandleExit(object? sender, EventArgs e) => Exited?.Invoke();

    public static async Task<(bool Success, string Message)> ValidateAsync(string executable, string configPath, CancellationToken cancellationToken = default)
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, BundledResources.CoreFileName);
        if (string.Equals(Path.GetFullPath(executable), bundledPath, StringComparison.OrdinalIgnoreCase))
        {
            try { await BundledResources.EnsureCoreAsync(cancellationToken); }
            catch (IOException error) { return (false, error.Message); }
            catch (UnauthorizedAccessException error) { return (false, error.Message); }
        }
        var info = CreateStartInfo(executable, configPath);
        info.ArgumentList.Insert(0, "-t");
        using var process = new Process { StartInfo = info };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var text = (await stdout + Environment.NewLine + await stderr).Trim();
            return (process.ExitCode == 0, text.Length > 4000 ? text[^4000..] : text);
        }
        catch (OperationCanceledException)
        {
            var kill = Task.Run(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            await Task.WhenAny(kill, Task.Delay(2000));
            cancellationToken.ThrowIfCancellationRequested();
            return (false, "Проверка конфигурации превысила 15 секунд.");
        }
    }

    public static ProcessStartInfo CreateStartInfo(string executable, string configPath)
    {
        var fullConfigPath = Path.GetFullPath(configPath);
        var profileDirectory = Path.GetDirectoryName(fullConfigPath)!;
        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = profileDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        // Mihomo's configuration file (-f) and data home (-d) are independent.
        // Keep providers, geodata and caches next to the selected profile instead
        // of silently sharing an unrelated user's default Mihomo home.
        info.ArgumentList.Add("-d");
        info.ArgumentList.Add(profileDirectory);
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add(fullConfigPath);
        return info;
    }
}
