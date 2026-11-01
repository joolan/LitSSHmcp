using System.Text;

namespace LitSSHmcp.Core.Services.Resource;

public static class ResourceAnalysisCommands
{
    public const string Quick = """
LC_ALL=C
echo '##meta'
hostname 2>/dev/null
date '+%Y-%m-%dT%H:%M:%S%z' 2>/dev/null
( . /etc/os-release 2>/dev/null; printf 'OS=%s\n' "$PRETTY_NAME" )
uname -r 2>/dev/null
nproc 2>/dev/null
grep -m1 'model name' /proc/cpuinfo 2>/dev/null
echo '##stat1'
cat /proc/stat 2>/dev/null
echo '##load'
cat /proc/loadavg 2>/dev/null
echo '##mem'
free -m 2>/dev/null || cat /proc/meminfo 2>/dev/null
echo '##df'
df -hP -x tmpfs -x devtmpfs -x squashfs 2>/dev/null || df -hP 2>/dev/null
echo '##dfi'
df -iP -x tmpfs -x devtmpfs -x squashfs 2>/dev/null || df -iP 2>/dev/null
echo '##mounts'
findmnt -rno TARGET,OPTIONS 2>/dev/null || awk '{print $2"\t"$4}' /proc/mounts 2>/dev/null
echo '##ps'
ps -e -o pid= -o user= -o pcpu= -o pmem= -o rss= -o etime= -o args= 2>/dev/null
echo '##vmstat'
vmstat 1 2 2>/dev/null
echo '##stat2'
cat /proc/stat 2>/dev/null
echo '##iostat'
if command -v iostat >/dev/null 2>&1; then timeout 8 iostat -dx 1 2 2>/dev/null; fi
echo '##psi'
cat /proc/pressure/cpu 2>/dev/null
cat /proc/pressure/memory 2>/dev/null
cat /proc/pressure/io 2>/dev/null
echo '##oom'
dmesg -T 2>/dev/null | grep -i -E 'out of memory|oom-kill|killed process' | tail -n 20
timeout 10 journalctl -k --no-pager 2>/dev/null | grep -i -E 'out of memory|oom-kill|killed process' | tail -n 20
echo '##journal'
journalctl --disk-usage 2>/dev/null
echo '##docker'
if command -v docker >/dev/null 2>&1; then timeout 10 docker system df 2>/dev/null; fi
echo '##end'
""";

    public const string DiskDeep = """
LC_ALL=C
echo '##du'
timeout 25 nice -n 10 du -x -k -d 2 / 2>/dev/null | sort -nr | head -n 50
echo '##large'
timeout 25 find / -xdev -type f -size +200M -printf '%s\t%p\n' 2>/dev/null | sort -nr | head -n 30
echo '##deleted'
if command -v lsof >/dev/null 2>&1; then
  timeout 15 lsof +L1 2>/dev/null | awk 'NR>1 {print $1"\t"$2"\t"$7"\t"$9}' | head -n 30
fi
echo '##journal'
journalctl --disk-usage 2>/dev/null
echo '##docker'
if command -v docker >/dev/null 2>&1; then timeout 15 docker system df -v 2>/dev/null; fi
echo '##end'
""";

    public static string BuildAttribution(IEnumerable<int> pids)
    {
        var builder = new StringBuilder();
        builder.AppendLine("echo '##cgroups'");
        foreach (var pid in pids.Distinct().Where(pid => pid > 0).Take(30))
        {
            builder.AppendLine($"echo '##pid:{pid}'");
            builder.AppendLine($"cat /proc/{pid}/cgroup 2>/dev/null");
            builder.AppendLine($"grep -E '^(Name|VmSwap):' /proc/{pid}/status 2>/dev/null");
        }

        builder.AppendLine("echo '##dockerps'");
        builder.AppendLine("if command -v docker >/dev/null 2>&1; then timeout 10 docker ps --format '{{.ID}} {{.Names}}' 2>/dev/null; fi");
        builder.AppendLine("echo '##end'");
        return builder.ToString();
    }
}
