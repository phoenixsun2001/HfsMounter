using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace HfsMounter;

/// <summary>
/// 常驻循环：定时扫描物理磁盘 → 发现 HFS+ 卷就挂载，卷消失/读失败就卸载。
/// --daemon（前台调试）与 --service（SCM）跑的是同一套逻辑。
/// </summary>
public sealed class Supervisor : IDisposable
{
    private readonly AppConfig _config;
    private readonly Dictionary<string, MountedVolume> _mounted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RetryState> _retry = new(StringComparer.OrdinalIgnoreCase);
    private readonly MountState _state;
    private DateTime _lastAccessDeniedLogUtc = DateTime.MinValue;
    private DateTime _lastWinFspWarningUtc = DateTime.MinValue;
    private DateTime _lastLimitWarningUtc = DateTime.MinValue;
    private bool _disposed;

    private sealed class RetryState
    {
        public int Attempts;
        public DateTime NextAttemptUtc = DateTime.MinValue;
        public string LastError = "";
    }

    public Supervisor(AppConfig config)
    {
        _config = config;
        _state = MountState.Load();
    }

    public IReadOnlyDictionary<string, MountedVolume> Mounted => _mounted;

    public void Run(CancellationToken token)
    {
        Log.Info("HFS 常驻服务已启动");
        Log.Info($"  挂载根目录: {_config.MountRoot}");
        Log.Info($"  轮询间隔:   {_config.EffectivePollSeconds} 秒");
        Log.Info($"  最多挂载:   {_config.EffectiveMaxMounts} 个卷");

        if (!WinFspPresent(out string winFspDetail))
            Log.Error($"未检测到 WinFsp（{winFspDetail}）。挂载会一直等待，安装 WinFsp 后无需重启服务。");
        if (!DiskScanner.IsElevated)
            Log.Warn("当前进程没有管理员权限，无法读取物理磁盘。请以服务/管理员身份运行。");

        TimeSpan interval = TimeSpan.FromSeconds(_config.EffectivePollSeconds);
        while (!token.IsCancellationRequested)
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                Log.Error("轮询过程中出现未预期异常", ex);
            }

            if (token.WaitHandle.WaitOne(interval)) break;
        }

        StopAll("服务停止");
    }

    /// <summary>扫描一次并同步挂载状态（也供 --probe/--status 复用）。</summary>
    public void Poll()
    {
        if (!WinFspPresent(out string detail))
        {
            if (DateTime.UtcNow - _lastWinFspWarningUtc > TimeSpan.FromMinutes(5))
            {
                _lastWinFspWarningUtc = DateTime.UtcNow;
                Log.Warn($"WinFsp 不可用（{detail}），暂不挂载。安装后会自动生效。");
            }
            return;
        }

        DiskScanResult scan = DiskScanner.Scan();
        if (scan.AccessDenied)
        {
            if (DateTime.UtcNow - _lastAccessDeniedLogUtc > TimeSpan.FromMinutes(1))
            {
                _lastAccessDeniedLogUtc = DateTime.UtcNow;
                Log.Warn($"无法读取 {scan.AccessDeniedSample}（权限不足）：常驻服务需要以 SYSTEM/管理员身份运行");
            }
        }

        Reconcile(scan);
        MountNew(scan);
    }

    private void Reconcile(DiskScanResult scan)
    {
        var present = new HashSet<string>(scan.Candidates.Select(c => c.VolumeId), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _mounted.ToList())
        {
            string id = pair.Key;
            MountedVolume volume = pair.Value;

            if (!present.Contains(id))
            {
                Log.Info($"磁盘已拔出，卸载 {volume.MountPath}（{volume.Candidate.Describe()}）");
                Drop(id, volume);
                continue;
            }

            if (volume.HealthCheck())
            {
                volume.ConsecutiveHealthFailures = 0;
                continue;
            }

            volume.ConsecutiveHealthFailures++;
            if (volume.ConsecutiveHealthFailures >= _config.EffectiveRemovalGrace)
            {
                Log.Warn($"卷 {volume.MountPath} 连续 {volume.ConsecutiveHealthFailures} 次读取失败，判定磁盘已移除，开始卸载");
                Drop(id, volume);
            }
            else
            {
                Log.Debug($"卷 {volume.MountPath} 读取失败 {volume.ConsecutiveHealthFailures} 次，等待重试");
            }
        }
    }

    private void MountNew(DiskScanResult scan)
    {
        foreach (HfsCandidate candidate in scan.Candidates.OrderBy(c => c.DiskIndex).ThenBy(c => c.PartIndex))
        {
            if (_mounted.ContainsKey(candidate.VolumeId)) continue;

            if (_mounted.Count >= _config.EffectiveMaxMounts)
            {
                if (DateTime.UtcNow - _lastLimitWarningUtc > TimeSpan.FromMinutes(10))
                {
                    _lastLimitWarningUtc = DateTime.UtcNow;
                    Log.Warn($"已达到最大挂载数量 {_config.EffectiveMaxMounts}，忽略 {candidate.Describe()}");
                }
                continue;
            }

            if (_retry.TryGetValue(candidate.VolumeId, out RetryState? retry) && DateTime.UtcNow < retry.NextAttemptUtc)
                continue;

            MountOne(candidate);
        }
    }

    private void MountOne(HfsCandidate candidate)
    {
        string lastError = "";
        bool mountPointProblem = false;

        foreach (string path in PathCandidates(candidate.VolumeId).Take(_config.EffectiveMaxMounts + 3))
        {
            MountedVolume? volume = MountedVolume.TryMount(candidate, path, _config, out string error);
            if (volume != null)
            {
                _mounted[candidate.VolumeId] = volume;
                _retry.Remove(candidate.VolumeId);
                _state.Paths[candidate.VolumeId] = path;
                _state.Save();

                string name = volume.VolumeName != null ? $"「{volume.VolumeName}」" : "";
                Log.Info($"已挂载 {name}{candidate.Describe()} → {path}");
                return;
            }

            lastError = error;
            if (LooksLikeMountPointProblem(error))
            {
                mountPointProblem = true;
                Log.Warn($"挂载点 {path} 不可用（{error}），尝试下一个目录");
                continue;
            }

            // 磁盘层面的错误（卷头损坏、IO 错误、WinFsp 未安装等）：换目录也没用
            break;
        }

        var state = GetRetry(candidate.VolumeId);
        state.Attempts++;
        state.LastError = lastError;
        int delaySeconds = mountPointProblem
            ? Math.Min(30, 5 * state.Attempts)
            : Math.Min(300, 15 * state.Attempts);
        state.NextAttemptUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
        Log.Error($"挂载 {candidate.Describe()} 失败（第 {state.Attempts} 次，{delaySeconds}s 后重试）: {lastError}");
    }

    private static bool LooksLikeMountPointProblem(string error) =>
        error.Contains("Preflight", StringComparison.Ordinal) ||
        error.Contains("挂载点", StringComparison.Ordinal) ||
        error.Contains("挂载点不可用", StringComparison.Ordinal);

    private RetryState GetRetry(string volumeId)
    {
        if (!_retry.TryGetValue(volumeId, out RetryState? state))
        {
            state = new RetryState();
            _retry[volumeId] = state;
        }
        return state;
    }

    private IEnumerable<string> PathCandidates(string volumeId)
    {
        var used = new HashSet<string>(_mounted.Values.Select(v => v.MountPath), StringComparer.OrdinalIgnoreCase);

        if (_state.Paths.TryGetValue(volumeId, out string? previous) && !used.Contains(previous))
            yield return previous;

        string root = _config.MountRoot;
        for (int i = 1; i <= 26; i++)
        {
            string path = i == 1 ? root : root + i.ToString();
            if (!used.Contains(path)) yield return path;
        }
    }

    private void Drop(string volumeId, MountedVolume volume)
    {
        _mounted.Remove(volumeId);
        volume.Unmount();
        volume.Dispose();
        _state.Paths.Remove(volumeId);
        _state.Save();
    }

    private void StopAll(string reason)
    {
        if (_mounted.Count == 0)
        {
            Log.Info($"{reason}，没有需要卸载的卷");
            return;
        }

        Log.Info($"{reason}，卸载 {_mounted.Count} 个卷");
        foreach (var pair in _mounted.ToList())
        {
            try
            {
                Drop(pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                Log.Warn($"卸载 {pair.Value.MountPath} 失败: {ex.Message}");
            }
        }
    }

    /// <summary>WinFsp 是否已安装（只检查是不是有装载器/驱动，不加载它）。</summary>
    public static bool WinFspPresent(out string detail)
    {
        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string loader = Path.Combine(system32, "winfsp-x64.dll");
        string driver = Path.Combine(system32, "drivers", "winfsp.sys");
        bool hasLoader = File.Exists(loader);
        bool hasDriver = File.Exists(driver);
        detail = $"loader={hasLoader} driver={hasDriver}";

        if (hasLoader && hasDriver) return true;

        // 有些安装把文件放在 Program Files\WinFsp 下
        string alt = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp", "bin", "winfsp-x64.dll");
        if (File.Exists(alt))
        {
            detail += $", alt={alt}";
            return hasDriver;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAll("释放资源");
    }
}
