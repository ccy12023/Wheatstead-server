using System.Diagnostics;
using System.Globalization;

// 本机资源只在已认证的后台请求时读取；不可读取的指标返回 null。
internal sealed class ServerMonitor
{
    private readonly object _gate = new();
    private (ulong Total, ulong Idle, long Tick)? _previous;
    private (string Processes, ulong Usage, long Tick)? _previousWorld;
    private const string WorldCgroup = "/sys/fs/cgroup/system.slice/wheatstead-world.service";
    private readonly string _dataPath;

    public ServerMonitor(string dataPath)
    {
        _dataPath = Path.GetFullPath(dataPath);
        _previous = ReadCpu();
    }

    public object Snapshot()
    {
        double? cpu = null;
        double? worldCpu = null;
        long? worldMemory = null;
        lock (_gate)
        {
            var current = ReadCpu();
            if (current is { } now && _previous is { } previous && now.Tick - previous.Tick >= 1000)
            {
                if (now.Total > previous.Total && now.Idle >= previous.Idle && now.Idle - previous.Idle <= now.Total - previous.Total)
                    cpu = Math.Round(100.0 * (1.0 - (double)(now.Idle - previous.Idle) / (now.Total - previous.Total)), 1);
                _previous = current;
            }
            else if (current != null && _previous == null) _previous = current;
            var world = ReadWorld();
            if (world is { } sample)
            {
                worldMemory = sample.Memory;
                if (sample.Usage is { } usage)
                {
                    var retainPrevious = false;
                    if (_previousWorld is { } prior && prior.Processes == sample.Processes && usage >= prior.Usage)
                    {
                        var seconds = (double)(sample.Tick - prior.Tick) / Stopwatch.Frequency;
                        if (seconds >= 1)
                            worldCpu = Math.Round((usage - prior.Usage) / (seconds * 1_000_000) * 100, 1);
                        else if (seconds >= 0) retainPrevious = true;
                    }
                    if (!retainPrevious) _previousWorld = (sample.Processes, usage, sample.Tick);
                }
                else _previousWorld = null;
            }
            else _previousWorld = null;
        }
        var (memoryTotal, memoryAvailable) = ReadMemory();
        var (diskTotal, diskAvailable) = ReadDisk();
        long? processMemory = null, uptimeSeconds = null;
        try
        {
            using var process = Process.GetCurrentProcess();
            processMemory = process.WorkingSet64;
            uptimeSeconds = Math.Max(0, (long)(DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        return new { sampled_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cpu_percent = cpu,
            memory_total_bytes = memoryTotal, memory_available_bytes = memoryAvailable,
            disk_total_bytes = diskTotal, disk_available_bytes = diskAvailable,
            api_memory_bytes = processMemory, api_uptime_seconds = uptimeSeconds,
            world_memory_bytes = worldMemory, world_cpu_percent = worldCpu };
    }

    private static (string Processes, ulong? Usage, long? Memory, long Tick)? ReadWorld()
    {
        try
        {
            static string Processes()
            {
                var ids = File.ReadAllLines(Path.Combine(WorldCgroup, "cgroup.procs"));
                if (ids.Length == 0 || ids.Any(id => !uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == 0))
                    return "";
                return string.Join(',', ids.Order(StringComparer.Ordinal));
            }

            var before = Processes();
            if (before.Length == 0) return null;
            long? memory = null;
            ulong? usage = null;
            try
            {
                if (long.TryParse(File.ReadAllText(Path.Combine(WorldCgroup, "memory.current")).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    memory = value;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            try
            {
                var line = File.ReadLines(Path.Combine(WorldCgroup, "cpu.stat")).FirstOrDefault(x => x.StartsWith("usage_usec ", StringComparison.Ordinal));
                if (line != null && ulong.TryParse(line.AsSpan("usage_usec ".Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    usage = value;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            var tick = Stopwatch.GetTimestamp();
            return before == Processes() ? (before, usage, memory, tick) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static (ulong Total, ulong Idle, long Tick)? ReadCpu()
    {
        try
        {
            var fields = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 || fields[0] != "cpu") return null;
            // guest 时间已经包含在 user / nice 中，不能重复相加。
            var values = fields.Skip(1).Take(8).Select(x => ulong.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            return (values.Aggregate(0UL, (sum, value) => checked(sum + value)),
                checked(values[3] + (values.Length > 4 ? values[4] : 0)), Environment.TickCount64);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or OverflowException or InvalidOperationException) { return null; }
    }

    private static (long?, long?) ReadMemory()
    {
        try
        {
            long? total = null, available = null;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || parts[2] != "kB") continue;
                if (parts[0] != "MemTotal:" && parts[0] != "MemAvailable:") continue;
                var value = checked(long.Parse(parts[1], CultureInfo.InvariantCulture) * 1024);
                if (parts[0] == "MemTotal:") total = value; else available = value;
            }
            if (total is <= 0 || available < 0 || available > total) return (null, null);
            return (total, available);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or OverflowException) { return (null, null); }
    }

    private (long?, long?) ReadDisk()
    {
        try
        {
            var drive = DriveInfo.GetDrives().Where(d => _dataPath.StartsWith(d.Name.TrimEnd('/') + "/", StringComparison.Ordinal))
                .OrderByDescending(d => d.Name.Length).FirstOrDefault();
            return drive == null || !drive.IsReady ? (null, null) : (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { return (null, null); }
    }
}
