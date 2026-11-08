using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.Core.Services.Kubernetes;

public sealed class KubernetesCommandException : Exception
{
    public KubernetesCommandException(string message) : base(message) { }
}

public sealed class KubernetesTestResult
{
    public string Version { get; init; } = string.Empty;
    public string Distribution { get; init; } = string.Empty;
    public int NamespaceCount { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class KubernetesDashboard
{
    public string Version { get; init; } = string.Empty;
    public string Distribution { get; init; } = string.Empty;
    public int NodeCount { get; init; }
    public int ReadyNodeCount { get; init; }
    public int NamespaceCount { get; init; }
    public int PodCount { get; init; }
    public int RunningPodCount { get; init; }
    public int WarningEventCount { get; init; }
    public List<KubernetesNodeInfo> Nodes { get; init; } = new();
    public List<KubernetesServiceInfo> Services { get; init; } = new();
    public List<KubernetesIngressInfo> Ingresses { get; init; } = new();
    public List<KubernetesPvcInfo> Pvcs { get; init; } = new();
    public List<KubernetesStorageClassInfo> StorageClasses { get; init; } = new();
    public KubernetesMetricsSnapshot? Metrics { get; init; }
    public List<KubernetesNamespaceInfo> Namespaces { get; init; } = new();
    public List<KubernetesWorkloadInfo> Workloads { get; init; } = new();
    public List<KubernetesPodInfo> Pods { get; init; } = new();
    public List<KubernetesEventInfo> Events { get; init; } = new();
}

public sealed class KubernetesReleaseInventory
{
    public List<KubernetesWorkloadInfo> Workloads { get; init; } = new();
    public List<KubernetesIngressInfo> Ingresses { get; init; } = new();
}

public sealed class KubernetesNodeInfo
{
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Roles { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Cpu { get; init; } = string.Empty;
    public string Memory { get; init; } = string.Empty;
    public string CpuUsage { get; set; } = string.Empty;
    public string MemoryUsage { get; set; } = string.Empty;
    public string CpuDisplay { get; set; } = string.Empty;
    public string CpuUsageDisplay { get; set; } = string.Empty;
    public string MemoryDisplay { get; set; } = string.Empty;
    public string MemoryUsageDisplay { get; set; } = string.Empty;
    public string CpuUsageTooltip { get; set; } = string.Empty;
    public string MemoryUsageTooltip { get; set; } = string.Empty;
    public int Pods { get; init; }
    public string Age { get; init; } = string.Empty;
}

public sealed class KubernetesMetricsSnapshot
{
    public List<KubernetesNodeMetric> Nodes { get; init; } = new();
    public List<KubernetesPodMetric> Pods { get; init; } = new();
}

public sealed class KubernetesNodeMetric
{
    public string Name { get; init; } = string.Empty;
    public string Cpu { get; init; } = string.Empty;
    public string Memory { get; init; } = string.Empty;
}

public sealed class KubernetesPodMetric
{
    public string Namespace { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Cpu { get; init; } = string.Empty;
    public string Memory { get; init; } = string.Empty;
}

public sealed class KubernetesNamespaceInfo
{
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Age { get; init; } = string.Empty;
}

public sealed class KubernetesWorkloadInfo
{
    public string Kind { get; init; } = string.Empty;
    public string Namespace { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Ready { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Age { get; init; } = string.Empty;
    public int DesiredReplicas { get; init; }
    public int ReadyReplicas { get; init; }
    public string Resources { get; init; } = string.Empty;
    public Dictionary<string, string> Selector { get; init; } = new();
}

public sealed class KubernetesPodInfo
{
    public string Namespace { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Ready { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int Restarts { get; init; }
    public string Node { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public string Age { get; init; } = string.Empty;
    public string CpuUsage { get; set; } = string.Empty;
    public string MemoryUsage { get; set; } = string.Empty;
    public string CpuUsageDisplay { get; set; } = string.Empty;
    public string MemoryUsageDisplay { get; set; } = string.Empty;
    public string CpuUsageTooltip { get; set; } = string.Empty;
    public string MemoryUsageTooltip { get; set; } = string.Empty;
    public string CpuLimit { get; init; } = string.Empty;
    public string MemoryLimit { get; init; } = string.Empty;
    public string OwnerKind { get; init; } = string.Empty;
    public string OwnerName { get; init; } = string.Empty;
    public Dictionary<string, string> Labels { get; init; } = new();
    public List<string> Containers { get; init; } = new();
}

public sealed class KubernetesEventInfo
{
    public string Type { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Namespace { get; init; } = string.Empty;
    public string Object { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int Count { get; init; }
    public string Age { get; init; } = string.Empty;
}

public sealed class KubernetesServiceInfo
{
    public string Namespace { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string ClusterIp { get; init; } = string.Empty;
    public string ExternalIp { get; init; } = string.Empty;
    public string Ports { get; init; } = string.Empty;
    public string Age { get; init; } = string.Empty;
    public Dictionary<string, string> Selector { get; init; } = new();
}

public sealed class KubernetesIngressInfo
{
    public string Namespace { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string Hosts { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string Age { get; init; } = string.Empty;
    public List<string> ServiceNames { get; init; } = new();
}

public sealed class KubernetesPvcInfo
{
    public string Namespace { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Volume { get; init; } = string.Empty;
    public string Capacity { get; init; } = string.Empty;
    public string StorageClass { get; init; } = string.Empty;
    public string Age { get; init; } = string.Empty;
}

public sealed class KubernetesStorageClassInfo
{
    public string Name { get; init; } = string.Empty;
    public string Provisioner { get; init; } = string.Empty;
    public string ReclaimPolicy { get; init; } = string.Empty;
    public string Default { get; init; } = string.Empty;
    public string Age { get; init; } = string.Empty;
}

public sealed class KubernetesService
{
    private readonly ISshService _sshService;
    private readonly SemaphoreSlim _remoteGate = new(1, 1);

    public KubernetesService(ISshService sshService)
    {
        _sshService = sshService;
    }

    public async Task<KubernetesTestResult> TestConnectionAsync(
        KubernetesConnectionConfig config,
        SshServerConfig? sshServer,
        CancellationToken ct = default)
    {
        var versionJson = await RunAsync(config, sshServer, new[] { "version", "-o", "json" }, ct);
        var version = ParseVersion(versionJson);
        var distribution = DetectDistribution(versionJson);
        var namespacesJson = await RunAsync(config, sshServer, new[] { "get", "namespaces", "-o", "json" }, ct);
        var namespaceCount = CountItems(namespacesJson);
        return new KubernetesTestResult
        {
            Version = version,
            Distribution = distribution,
            NamespaceCount = namespaceCount,
            Message = $"{distribution} {version}，可读取 {namespaceCount} 个命名空间"
        };
    }

    public async Task<KubernetesDashboard> LoadDashboardAsync(
        KubernetesConnectionConfig config,
        SshServerConfig? sshServer,
        CancellationToken ct = default)
    {
        // 合并查询：把 10 条 kubectl 请求降为 2 条（集群级 + 命名空间级），
        // 在远端串行 SSH 模式下可显著减少弱网等待。
        var versionTask = RunAsync(config, sshServer, new[] { "version", "-o", "json" }, ct);
        var clusterTask = RunAsync(config, sshServer,
            new[] { "get", "nodes,namespaces,storageclasses", "-o", "json" }, ct);
        var namespacedTask = RunAsync(config, sshServer,
            new[]
            {
                "get",
                "pods,deployments,statefulsets,daemonsets,cronjobs,events,services,ingresses,persistentvolumeclaims",
                "-A", "-o", "json"
            }, ct);

        var metricsTask = TryLoadMetricsAsync(config, sshServer, ct);
        await Task.WhenAll(versionTask, clusterTask, namespacedTask).ConfigureAwait(false);

        var metrics = await metricsTask.ConfigureAwait(false);
        var clusterJson = await clusterTask.ConfigureAwait(false);
        var namespacedJson = await namespacedTask.ConfigureAwait(false);
        var pods = ParsePods(FilterItemsByKind(namespacedJson, "Pod"));
        var nodes = ParseNodes(FilterItemsByKind(clusterJson, "Node"), pods);
        var namespaces = ParseNamespaces(FilterItemsByKind(clusterJson, "Namespace"));
        var workloads = ParseWorkloads(FilterItemsByKind(namespacedJson,
            "Deployment", "StatefulSet", "DaemonSet", "CronJob"));
        var events = ParseEvents(FilterItemsByKind(namespacedJson, "Event"));
        var services = ParseServices(FilterItemsByKind(namespacedJson, "Service"));
        var ingresses = ParseIngresses(FilterItemsByKind(namespacedJson, "Ingress"));
        var pvcs = ParsePvcs(FilterItemsByKind(namespacedJson, "PersistentVolumeClaim"));
        var storageClasses = ParseStorageClasses(FilterItemsByKind(clusterJson, "StorageClass"));
        var versionJson = await versionTask.ConfigureAwait(false);

        return new KubernetesDashboard
        {
            Version = ParseVersion(versionJson),
            Distribution = DetectDistribution(versionJson),
            Nodes = nodes,
            Namespaces = namespaces,
            Workloads = workloads,
            Pods = pods,
            Events = events,
            Services = services,
            Ingresses = ingresses,
            Pvcs = pvcs,
            StorageClasses = storageClasses,
            Metrics = metrics,
            NodeCount = nodes.Count,
            ReadyNodeCount = nodes.Count(item => string.Equals(item.Status, "Ready", StringComparison.Ordinal)),
            NamespaceCount = namespaces.Count,
            PodCount = pods.Count,
            RunningPodCount = pods.Count(item => string.Equals(item.Status, "Running", StringComparison.Ordinal)),
            WarningEventCount = events.Count(item => string.Equals(item.Type, "Warning", StringComparison.Ordinal))
        };
    }

    private async Task<KubernetesMetricsSnapshot?> TryLoadMetricsAsync(
        KubernetesConnectionConfig config,
        SshServerConfig? sshServer,
        CancellationToken ct)
    {
        try
        {
            var nodesTask = RunAsync(config, sshServer, new[] { "top", "nodes", "--no-headers" }, ct);
            var podsTask = RunAsync(config, sshServer, new[] { "top", "pods", "-A", "--no-headers" }, ct);
            await Task.WhenAll(nodesTask, podsTask).ConfigureAwait(false);
            return new KubernetesMetricsSnapshot
            {
                Nodes = ParseNodeMetrics(await nodesTask.ConfigureAwait(false)),
                Pods = ParsePodMetrics(await podsTask.ConfigureAwait(false))
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static List<KubernetesNodeMetric> ParseNodeMetrics(string text)
    {
        var result = new List<KubernetesNodeMetric>();
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || fields[0] == "NAME")
                continue;
            result.Add(new KubernetesNodeMetric
            {
                Name = fields[0],
                Cpu = fields[1],
                Memory = fields[3]
            });
        }
        return result;
    }

    private static List<KubernetesPodMetric> ParsePodMetrics(string text)
    {
        var result = new List<KubernetesPodMetric>();
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || fields[0] == "NAMESPACE")
                continue;
            result.Add(new KubernetesPodMetric
            {
                Namespace = fields[0],
                Name = fields[1],
                Cpu = fields[2],
                Memory = fields[3]
            });
        }
        return result;
    }
    public async Task<KubernetesReleaseInventory> LoadReleaseInventoryAsync(
        KubernetesConnectionConfig config,
        SshServerConfig? sshServer,
        CancellationToken ct = default)
    {
        var workloadsTask = RunAsync(config, sshServer,
            new[] { "get", "deployments,statefulsets", "-A", "-o", "json" }, ct);
        var ingressesTask = RunAsync(config, sshServer,
            new[] { "get", "ingresses", "-A", "-o", "json" }, ct);
        await Task.WhenAll(workloadsTask, ingressesTask).ConfigureAwait(false);
        return new KubernetesReleaseInventory
        {
            Workloads = ParseWorkloads(await workloadsTask.ConfigureAwait(false)),
            Ingresses = ParseIngresses(await ingressesTask.ConfigureAwait(false))
        };
    }

    public Task<string> GetPodLogsAsync(
        KubernetesConnectionConfig config,
        SshServerConfig? sshServer,
        KubernetesPodInfo pod,
        string? container = null,
        bool previous = false,
        CancellationToken ct = default)
    {
        var args = new List<string> { "logs", pod.Name, "-n", pod.Namespace, "--tail=600" };
        if (!string.IsNullOrWhiteSpace(container))
        {
            args.Add("--container");
            args.Add(container);
        }
        if (previous)
            args.Add("--previous");
        return RunAsync(config, sshServer, args, ct);
    }

    public Task<string> GetPodYamlAsync(
        KubernetesConnectionConfig config,
        SshServerConfig? sshServer,
        KubernetesPodInfo pod,
        CancellationToken ct = default)
        => RunAsync(config, sshServer,
            new[] { "get", "pod", pod.Name, "-n", pod.Namespace, "-o", "yaml" }, ct);

    public Task<string> GetWorkloadYamlAsync(
        KubernetesConnectionConfig config,
        SshServerConfig? sshServer,
        KubernetesWorkloadInfo workload,
        CancellationToken ct = default)
        => RunAsync(config, sshServer,
            new[] { "get", workload.Kind.ToLowerInvariant(), workload.Name, "-n", workload.Namespace, "-o", "yaml" }, ct);

    public Task<string> GetServiceYamlAsync(
        KubernetesConnectionConfig config,
        SshServerConfig? sshServer,
        KubernetesServiceInfo service,
        CancellationToken ct = default)
        => RunAsync(config, sshServer,
            new[] { "get", "service", service.Name, "-n", service.Namespace, "-o", "yaml" }, ct);

    public Task<string> ScaleWorkloadAsync(
        KubernetesConnectionConfig config,
        SshServerConfig? sshServer,
        KubernetesWorkloadInfo workload,
        int replicas,
        CancellationToken ct = default)
    {
        if (replicas < 0 || replicas > 1000)
            throw new KubernetesCommandException("副本数必须在 0～1000 之间。");
        return RunAsync(config, sshServer, new[]
        {
            "scale", $"{workload.Kind.ToLowerInvariant()}/{workload.Name}",
            "--replicas", replicas.ToString(CultureInfo.InvariantCulture),
            "-n", workload.Namespace
        }, ct);
    }

    public Task<string> RestartWorkloadAsync(
        KubernetesConnectionConfig config,
        SshServerConfig? sshServer,
        KubernetesWorkloadInfo workload,
        CancellationToken ct = default)
        => RunAsync(config, sshServer, new[]
        {
            "rollout", "restart", $"{workload.Kind.ToLowerInvariant()}/{workload.Name}",
            "-n", workload.Namespace
        }, ct);

    public Task<string> RollbackWorkloadAsync(
        KubernetesConnectionConfig config,
        SshServerConfig? sshServer,
        KubernetesWorkloadInfo workload,
        CancellationToken ct = default)
    {
        if (!string.Equals(workload.Kind, "Deployment", StringComparison.OrdinalIgnoreCase))
            throw new KubernetesCommandException("当前只支持 Deployment 回滚。");
        return RunAsync(config, sshServer, new[]
        {
            "rollout", "undo", $"deployment/{workload.Name}", "-n", workload.Namespace
        }, ct);
    }

    public Task<string> DeletePodAsync(
        KubernetesConnectionConfig config,
        SshServerConfig? sshServer,
        KubernetesPodInfo pod,
        CancellationToken ct = default)
        => RunAsync(config, sshServer, new[]
        {
            "delete", "pod", pod.Name, "-n", pod.Namespace, "--wait=false"
        }, ct);

    public Task<string> CordonNodeAsync(
        KubernetesConnectionConfig config,
        SshServerConfig? sshServer,
        string nodeName,
        bool cordon,
        CancellationToken ct = default)
        => RunAsync(config, sshServer, new[] { cordon ? "cordon" : "uncordon", nodeName }, ct);

    public async Task<bool> CanIAsync(
        KubernetesConnectionConfig config,
        SshServerConfig? sshServer,
        string verb,
        string resource,
        string? namespaceName,
        CancellationToken ct = default)
    {
        var args = new List<string> { "auth", "can-i", verb, resource };
        if (!string.IsNullOrWhiteSpace(namespaceName))
        {
            args.Add("-n");
            args.Add(namespaceName);
        }
        try
        {
            var output = await RunAsync(config, sshServer, args, ct).ConfigureAwait(false);
            return output.Trim().StartsWith("yes", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
    private async Task<string> RunAsync(
        KubernetesConnectionConfig config,
        SshServerConfig? sshServer,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        return config.IsRemote
            ? await RunRemoteAsync(config, sshServer, args, ct).ConfigureAwait(false)
            : await RunLocalAsync(config, args, ct).ConfigureAwait(false);
    }

    private async Task<string> RunLocalAsync(
        KubernetesConnectionConfig config,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        string? tempKubeconfig = null;
        try
        {
            var kubeconfigPath = Environment.ExpandEnvironmentVariables(config.KubeconfigPath.Trim());
            if (!string.IsNullOrWhiteSpace(config.KubeconfigContent))
            {
                tempKubeconfig = Path.Combine(Path.GetTempPath(), $"litssh-k8s-{Guid.NewGuid():N}.yaml");
                await File.WriteAllTextAsync(tempKubeconfig, config.KubeconfigContent, new UTF8Encoding(false), ct)
                    .ConfigureAwait(false);
                kubeconfigPath = tempKubeconfig;
            }

            var executable = string.IsNullOrWhiteSpace(config.KubectlPath) ? "kubectl" : config.KubectlPath.Trim();
            var psi = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var arg in BuildCommonArgs(config, remote: false))
                psi.ArgumentList.Add(arg);
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            psi.ArgumentList.Add("--request-timeout");
            psi.ArgumentList.Add(Math.Max(5, config.TimeoutSeconds) + "s");

            if (!string.IsNullOrWhiteSpace(kubeconfigPath))
                psi.Environment["KUBECONFIG"] = kubeconfigPath;

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new KubernetesCommandException(
                    $"无法启动 kubectl（{executable}）。请确认本机已安装 kubectl，或在连接配置中填写完整路径。{ex.Message}");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, config.TimeoutSeconds)));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new KubernetesCommandException($"kubectl 执行超时（{config.TimeoutSeconds} 秒）。");
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new KubernetesCommandException(DescribeFailure(stderr, stdout, process.ExitCode));
            return stdout;
        }
        finally
        {
            if (tempKubeconfig is not null)
            {
                try { File.Delete(tempKubeconfig); } catch { }
            }
        }
    }

    private async Task<string> RunRemoteAsync(
        KubernetesConnectionConfig config,
        SshServerConfig? sshServer,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (sshServer is null)
            throw new KubernetesCommandException("远端 kubectl 模式缺少有效的 SSH 服务器。");

        var executable = string.IsNullOrWhiteSpace(config.RemoteKubectlPath)
            ? "kubectl"
            : config.RemoteKubectlPath.Trim();
        var parts = new List<string> { Quote(executable) };
        foreach (var arg in BuildCommonArgs(config, remote: true))
            parts.Add(Quote(arg));
        foreach (var arg in args)
            parts.Add(Quote(arg));
        parts.Add(Quote("--request-timeout"));
        parts.Add(Quote(Math.Max(5, config.TimeoutSeconds) + "s"));

        await _remoteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await _sshService.ExecuteCommandAsync(
                sshServer, string.Join(" ", parts), ct, Math.Max(10, config.TimeoutSeconds + 10))
                .ConfigureAwait(false);
            if (!result.Success)
                throw new KubernetesCommandException(DescribeFailure(result.Error, result.Output, result.ExitCode));
            return result.Output;
        }
        finally
        {
            _remoteGate.Release();
        }
    }

    private static IEnumerable<string> BuildCommonArgs(KubernetesConnectionConfig config, bool remote)
    {
        if (remote && !string.IsNullOrWhiteSpace(config.RemoteKubeconfigPath))
        {
            yield return "--kubeconfig";
            yield return config.RemoteKubeconfigPath.Trim();
        }
        if (!string.IsNullOrWhiteSpace(config.ContextName))
        {
            yield return "--context";
            yield return config.ContextName.Trim();
        }
    }

    private static string DescribeFailure(string? stderr, string? stdout, int exitCode)
    {
        var text = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
            return $"kubectl 执行失败（exit {exitCode}）。";

        if (text.Contains("executable file not found", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("is not recognized", StringComparison.OrdinalIgnoreCase))
            return "找不到 kubectl。请在连接配置中填写 kubectl 路径，并确认该路径可执行。";
        if (text.Contains("Unable to connect to the server", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("i/o timeout", StringComparison.OrdinalIgnoreCase))
            return $"无法连接 Kubernetes API Server。请检查网络、私网访问或 SSH 跳板配置。{Environment.NewLine}{text}";
        if (text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
            return $"Kubernetes 认证或权限不足。请检查 kubeconfig、context 与 RBAC。{Environment.NewLine}{text}";
        if (text.Contains("context", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return $"kubeconfig 中找不到指定 context。{Environment.NewLine}{text}";
        return text;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string ParseVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("serverVersion", out var server) &&
            server.ValueKind == JsonValueKind.Object &&
            server.TryGetProperty("gitVersion", out var serverVersion))
            return serverVersion.GetString() ?? "Kubernetes";
        if (root.TryGetProperty("clientVersion", out var client) &&
            client.ValueKind == JsonValueKind.Object &&
            client.TryGetProperty("gitVersion", out var clientVersion))
            return clientVersion.GetString() ?? "Kubernetes";
        return "Kubernetes";
    }

    private static int CountItems(string json)
    {
        using var document = JsonDocument.Parse(json);
        return GetItems(document.RootElement).Count();
    }

    /// <summary>从合并查询的 List 结果中按 kind 过滤出子集，保持原有解析器不变。</summary>
    private static string FilterItemsByKind(string json, params string[] kinds)
    {
        if (kinds.Length == 0)
            return json;

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            return json;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "List");
            writer.WriteStartArray("items");
            foreach (var item in items.EnumerateArray())
            {
                var kind = GetString(item, "kind");
                if (kinds.Any(candidate => string.Equals(candidate, kind, StringComparison.OrdinalIgnoreCase)))
                    item.WriteTo(writer);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static List<KubernetesNodeInfo> ParseNodes(string json, IReadOnlyList<KubernetesPodInfo> pods)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<KubernetesNodeInfo>();
        foreach (var item in GetItems(document.RootElement))
        {
            var name = GetString(item, "metadata", "name");
            var labels = GetObject(item, "metadata", "labels");
            var roles = new List<string>();
            if (labels is not null)
            {
                foreach (var label in labels.Value.EnumerateObject())
                {
                    const string prefix = "node-role.kubernetes.io/";
                    if (label.Name.StartsWith(prefix, StringComparison.Ordinal))
                        roles.Add(label.Name[prefix.Length..]);
                }
            }

            var ready = false;
            var status = "NotReady";
            var conditions = GetArray(item, "status", "conditions");
            if (conditions is not null)
            {
                foreach (var condition in conditions.Value.EnumerateArray())
                {
                    if (GetString(condition, "type") != "Ready")
                        continue;
                    ready = string.Equals(GetString(condition, "status"), "True", StringComparison.Ordinal);
                    status = ready ? "Ready" : (GetString(condition, "reason") is { Length: > 0 } reason ? reason : "NotReady");
                }
            }

            result.Add(new KubernetesNodeInfo
            {
                Name = name,
                Status = status,
                Roles = roles.Count == 0 ? "worker" : string.Join(", ", roles),
                Version = GetString(item, "status", "nodeInfo", "kubeletVersion"),
                Cpu = GetString(item, "status", "capacity", "cpu"),
                Memory = GetString(item, "status", "capacity", "memory"),
                Pods = pods.Count(pod => string.Equals(pod.Node, name, StringComparison.Ordinal)),
                Age = FormatAge(GetString(item, "metadata", "creationTimestamp"))
            });
        }
        return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<KubernetesNamespaceInfo> ParseNamespaces(string json)
    {
        using var document = JsonDocument.Parse(json);
        return GetItems(document.RootElement)
            .Select(item => new KubernetesNamespaceInfo
            {
                Name = GetString(item, "metadata", "name"),
                Status = GetString(item, "status", "phase"),
                Age = FormatAge(GetString(item, "metadata", "creationTimestamp"))
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<KubernetesWorkloadInfo> ParseWorkloads(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<KubernetesWorkloadInfo>();
        foreach (var item in GetItems(document.RootElement))
        {
            var kind = GetString(item, "kind");
            var desired = GetInt(item, "spec", "replicas");
            var ready = GetInt(item, "status", "readyReplicas");
            var available = GetInt(item, "status", "availableReplicas");
            var status = kind is "CronJob" or "DaemonSet"
                ? GetString(item, "status", "conditions", "0", "type") is { Length: > 0 } type ? type : "已部署"
                : desired == 0 ? "已停止" : ready >= desired ? "就绪" : "进度中";

            result.Add(new KubernetesWorkloadInfo
            {
                Kind = kind,
                Namespace = GetString(item, "metadata", "namespace"),
                Name = GetString(item, "metadata", "name"),
                Ready = kind is "CronJob" ? "计划任务" : kind is "DaemonSet" ? $"{ready} 节点" : $"{ready}/{Math.Max(desired, 0)}",
                Image = GetFirstContainerImage(item),
                Status = status,
                Age = FormatAge(GetString(item, "metadata", "creationTimestamp")),
                DesiredReplicas = desired,
                ReadyReplicas = ready,
                Resources = GetResources(item),
                Selector = ParseStringMap(GetObject(item, "spec", "selector", "matchLabels"))
            });
        }
        return result.OrderBy(item => item.Namespace, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<KubernetesPodInfo> ParsePods(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<KubernetesPodInfo>();
        foreach (var item in GetItems(document.RootElement))
        {
            var phase = GetString(item, "status", "phase");
            var waiting = string.Empty;
            var readyCount = 0;
            var restarts = 0;
            var containerStatuses = GetArray(item, "status", "containerStatuses");
            if (containerStatuses is not null)
            {
                foreach (var container in containerStatuses.Value.EnumerateArray())
                {
                    if (container.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True)
                        readyCount++;
                    restarts += GetInt(container, "restartCount");
                    var reason = GetString(container, "state", "waiting", "reason");
                    if (!string.IsNullOrWhiteSpace(reason))
                        waiting = reason;
                }
            }

            var status = phase switch
            {
                "Running" => string.IsNullOrWhiteSpace(waiting) ? "Running" : waiting,
                "Succeeded" => "Completed",
                "Failed" => string.IsNullOrWhiteSpace(waiting) ? "Failed" : waiting,
                "Pending" => string.IsNullOrWhiteSpace(waiting) ? "Pending" : waiting,
                _ => string.IsNullOrWhiteSpace(waiting) ? phase : waiting
            };

            var containerCount = GetArray(item, "spec", "containers") is { } containers
                ? containers.GetArrayLength()
                : 0;
            var containerNames = new List<string>();
            var specContainers = GetArray(item, "spec", "containers");
            var firstContainer = specContainers is { } podContainers && podContainers.GetArrayLength() > 0
                ? podContainers.EnumerateArray().First()
                : default;
            if (specContainers is not null)
            {
                foreach (var container in specContainers.Value.EnumerateArray())
                {
                    var containerName = GetString(container, "name");
                    if (!string.IsNullOrWhiteSpace(containerName))
                        containerNames.Add(containerName);
                }
            }
            var ownerReferences = GetArray(item, "metadata", "ownerReferences");
            var ownerKind = string.Empty;
            var ownerName = string.Empty;
            if (ownerReferences is not null && ownerReferences.Value.GetArrayLength() > 0)
            {
                var owner = ownerReferences.Value.EnumerateArray().First();
                ownerKind = GetString(owner, "kind");
                ownerName = GetString(owner, "name");
            }
            result.Add(new KubernetesPodInfo
            {
                Namespace = GetString(item, "metadata", "namespace"),
                Name = GetString(item, "metadata", "name"),
                Ready = $"{readyCount}/{containerCount}",
                Status = status,
                Restarts = restarts,
                Node = GetString(item, "spec", "nodeName"),
                Image = GetFirstContainerImage(item),
                Age = FormatAge(GetString(item, "metadata", "creationTimestamp")),
                OwnerKind = ownerKind,
                OwnerName = ownerName,
                Labels = ParseStringMap(GetObject(item, "metadata", "labels")),
                CpuLimit = firstContainer.ValueKind == JsonValueKind.Object ? GetString(firstContainer, "resources", "limits", "cpu") : string.Empty,
                MemoryLimit = firstContainer.ValueKind == JsonValueKind.Object ? GetString(firstContainer, "resources", "limits", "memory") : string.Empty,
                Containers = containerNames
            });
        }
        return result.OrderBy(item => item.Namespace, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<KubernetesEventInfo> ParseEvents(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<KubernetesEventInfo>();
        foreach (var item in GetItems(document.RootElement))
        {
            var type = GetString(item, "type");
            var reason = GetString(item, "reason");
            var message = GetString(item, "message");
            var count = GetInt(item, "count");
            var timestamp = GetString(item, "lastTimestamp");
            if (string.IsNullOrWhiteSpace(timestamp))
                timestamp = GetString(item, "eventTime");
            if (string.IsNullOrWhiteSpace(timestamp))
                timestamp = GetString(item, "metadata", "creationTimestamp");

            result.Add(new KubernetesEventInfo
            {
                Type = string.IsNullOrWhiteSpace(type) ? "Normal" : type,
                Reason = reason,
                Namespace = GetString(item, "metadata", "namespace"),
                Object = $"{GetString(item, "involvedObject", "kind")}/{GetString(item, "involvedObject", "name")}",
                Message = message,
                Count = Math.Max(1, count),
                Age = FormatAge(timestamp)
            });
        }
        return result.OrderByDescending(item => item.Age, StringComparer.Ordinal).ToList();
    }

    public static string DetectDistribution(string versionJson)
    {
        var version = ParseVersion(versionJson);
        if (version.Contains("+k3s", StringComparison.OrdinalIgnoreCase) ||
            version.Contains("k3s", StringComparison.OrdinalIgnoreCase))
            return "K3s";
        if (version.Contains("eks", StringComparison.OrdinalIgnoreCase))
            return "EKS";
        if (version.Contains("gke", StringComparison.OrdinalIgnoreCase))
            return "GKE";
        if (version.Contains("aks", StringComparison.OrdinalIgnoreCase))
            return "AKS";
        return "Kubernetes";
    }

    private static List<KubernetesServiceInfo> ParseServices(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<KubernetesServiceInfo>();
        foreach (var item in GetItems(document.RootElement))
        {
            var ports = new List<string>();
            var portArray = GetArray(item, "spec", "ports");
            if (portArray is not null)
            {
                foreach (var port in portArray.Value.EnumerateArray())
                {
                    var number = GetInt(port, "port");
                    var protocol = GetString(port, "protocol");
                    var name = GetString(port, "name");
                    var text = (name.Length > 0 ? name + ":" : string.Empty) + number +
                               (protocol.Length > 0 ? "/" + protocol : string.Empty);
                    ports.Add(text);
                }
            }

            var external = string.Empty;
            var loadBalancer = GetArray(item, "status", "loadBalancer", "ingress");
            if (loadBalancer is not null && loadBalancer.Value.GetArrayLength() > 0)
            {
                var first = loadBalancer.Value.EnumerateArray().First();
                external = GetString(first, "ip");
                if (string.IsNullOrWhiteSpace(external))
                    external = GetString(first, "hostname");
            }

            result.Add(new KubernetesServiceInfo
            {
                Namespace = GetString(item, "metadata", "namespace"),
                Name = GetString(item, "metadata", "name"),
                Type = GetString(item, "spec", "type"),
                ClusterIp = GetString(item, "spec", "clusterIP"),
                ExternalIp = string.IsNullOrWhiteSpace(external) ? "—" : external,
                Ports = ports.Count == 0 ? "—" : string.Join(", ", ports),
                Age = FormatAge(GetString(item, "metadata", "creationTimestamp")),
                Selector = ParseStringMap(GetObject(item, "spec", "selector"))
            });
        }
        return result.OrderBy(item => item.Namespace, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<KubernetesIngressInfo> ParseIngresses(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<KubernetesIngressInfo>();
        foreach (var item in GetItems(document.RootElement))
        {
            var hosts = new List<string>();
            var rules = GetArray(item, "spec", "rules");
            if (rules is not null)
            {
                foreach (var rule in rules.Value.EnumerateArray())
                {
                    var host = GetString(rule, "host");
                    hosts.Add(string.IsNullOrWhiteSpace(host) ? "*" : host);
                }
            }

            var address = string.Empty;
            var ingress = GetArray(item, "status", "loadBalancer", "ingress");
            if (ingress is not null && ingress.Value.GetArrayLength() > 0)
            {
                var first = ingress.Value.EnumerateArray().First();
                address = GetString(first, "ip");
                if (string.IsNullOrWhiteSpace(address))
                    address = GetString(first, "hostname");
            }

            var serviceNames = new List<string>();
            var defaultService = GetString(item, "spec", "defaultBackend", "service", "name");
            if (!string.IsNullOrWhiteSpace(defaultService))
                serviceNames.Add(defaultService);
            if (rules is not null)
            {
                foreach (var rule in rules.Value.EnumerateArray())
                {
                    var paths = GetArray(rule, "http", "paths");
                    if (paths is null)
                        continue;
                    foreach (var path in paths.Value.EnumerateArray())
                    {
                        var serviceName = GetString(path, "backend", "service", "name");
                        if (!string.IsNullOrWhiteSpace(serviceName) && !serviceNames.Contains(serviceName))
                            serviceNames.Add(serviceName);
                    }
                }
            }

            result.Add(new KubernetesIngressInfo
            {
                Namespace = GetString(item, "metadata", "namespace"),
                Name = GetString(item, "metadata", "name"),
                ClassName = GetString(item, "spec", "ingressClassName"),
                Hosts = hosts.Count == 0 ? "*" : string.Join(", ", hosts),
                Address = string.IsNullOrWhiteSpace(address) ? "—" : address,
                Age = FormatAge(GetString(item, "metadata", "creationTimestamp")),
                ServiceNames = serviceNames
            });
        }
        return result.OrderBy(item => item.Namespace, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<KubernetesPvcInfo> ParsePvcs(string json)
    {
        using var document = JsonDocument.Parse(json);
        return GetItems(document.RootElement)
            .Select(item => new KubernetesPvcInfo
            {
                Namespace = GetString(item, "metadata", "namespace"),
                Name = GetString(item, "metadata", "name"),
                Status = GetString(item, "status", "phase"),
                Volume = GetString(item, "spec", "volumeName"),
                Capacity = GetString(item, "status", "capacity", "storage"),
                StorageClass = GetString(item, "spec", "storageClassName"),
                Age = FormatAge(GetString(item, "metadata", "creationTimestamp"))
            })
            .OrderBy(item => item.Namespace, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<KubernetesStorageClassInfo> ParseStorageClasses(string json)
    {
        using var document = JsonDocument.Parse(json);
        return GetItems(document.RootElement)
            .Select(item =>
            {
                var annotations = GetObject(item, "metadata", "annotations");
                var isDefault = false;
                if (annotations is not null)
                {
                    isDefault = annotations.Value.EnumerateObject().Any(annotation =>
                        (annotation.Name is "storageclass.kubernetes.io/is-default-class" or
                            "storageclass.beta.kubernetes.io/is-default-class") &&
                        string.Equals(annotation.Value.GetString(), "true", StringComparison.OrdinalIgnoreCase));
                }

                return new KubernetesStorageClassInfo
                {
                    Name = GetString(item, "metadata", "name"),
                    Provisioner = GetString(item, "provisioner"),
                    ReclaimPolicy = GetString(item, "reclaimPolicy"),
                    Default = isDefault ? "是" : "否",
                    Age = FormatAge(GetString(item, "metadata", "creationTimestamp"))
                };
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<JsonElement> GetItems(JsonElement root)
    {
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            return items.EnumerateArray();
        return Array.Empty<JsonElement>();
    }

    private static JsonElement? GetObject(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
                return null;
        }
        return current;
    }

    private static JsonElement? GetArray(JsonElement element, params string[] path)
    {
        var value = GetObject(element, path);
        return value is { ValueKind: JsonValueKind.Array } ? value : null;
    }

    private static Dictionary<string, string> ParseStringMap(JsonElement? element)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in element.Value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                result[property.Name] = property.Value.GetString() ?? string.Empty;
        }
        return result;
    }
    private static string GetString(JsonElement element, params string[] path)
    {
        var value = GetObject(element, path);
        return value is { ValueKind: JsonValueKind.String } ? value.Value.GetString() ?? string.Empty : string.Empty;
    }

    private static int GetInt(JsonElement element, params string[] path)
    {
        var value = GetObject(element, path);
        return value is { ValueKind: JsonValueKind.Number } && value.Value.TryGetInt32(out var number) ? number : 0;
    }

    private static string GetResources(JsonElement item)
    {
        var containers = GetArray(item, "spec", "template", "spec", "containers");
        if (containers is null || containers.Value.GetArrayLength() == 0)
            return string.Empty;

        var first = containers.Value.EnumerateArray().First();
        var parts = new List<string>();
        var limits = GetObject(first, "resources", "limits");
        if (limits is not null)
        {
            var cpu = GetString(first, "resources", "limits", "cpu");
            var memory = GetString(first, "resources", "limits", "memory");
            if (cpu.Length > 0 || memory.Length > 0)
                parts.Add($"limit {cpu}/{memory}".TrimEnd('/'));
        }
        var requests = GetObject(first, "resources", "requests");
        if (requests is not null)
        {
            var cpu = GetString(first, "resources", "requests", "cpu");
            var memory = GetString(first, "resources", "requests", "memory");
            if (cpu.Length > 0 || memory.Length > 0)
                parts.Add($"request {cpu}/{memory}".TrimEnd('/'));
        }
        return string.Join(", ", parts);
    }
    private static string GetFirstContainerImage(JsonElement item)
    {
        var containers = GetArray(item, "spec", "template", "spec", "containers")
            ?? GetArray(item, "spec", "containers");
        if (containers is null)
            return string.Empty;
        var first = containers.Value.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Object ? GetString(first, "image") : string.Empty;
    }

    private static string FormatAge(string? timestamp)
    {
        if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            return string.Empty;
        var span = DateTimeOffset.UtcNow - value;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)} 个月";
        return $"{(int)(span.TotalDays / 365)} 年";
    }
}
