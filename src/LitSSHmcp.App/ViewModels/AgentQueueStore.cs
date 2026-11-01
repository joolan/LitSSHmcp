using System.IO;
using System.Text.Json;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

internal static class AgentQueueStore
{
    private sealed class PersistedQueueItem
    {
        public string Text { get; set; } = string.Empty;
        public string[] Attachments { get; set; } = Array.Empty<string>();
    }

    private static readonly ISecretProtector Protector = new DpapiSecretProtector();
    internal static string? RootOverride { get; set; }
    private static string Dir => RootOverride ?? Path.Combine(ConfigPaths.AppDataDir, "agent-queues");

    public static IReadOnlyList<QueuedAgentMessage> Load(long sessionId)
    {
        if (sessionId <= 0)
        {
            return Array.Empty<QueuedAgentMessage>();
        }

        var file = PathFor(sessionId);
        if (!File.Exists(file))
        {
            return Array.Empty<QueuedAgentMessage>();
        }

        try
        {
            var raw = File.ReadAllText(file);
            var json = Protector.Unprotect(raw) ?? string.Empty;
            var items = JsonSerializer.Deserialize<List<PersistedQueueItem>>(json);
            if (items is null)
            {
                return Array.Empty<QueuedAgentMessage>();
            }

            var result = new List<QueuedAgentMessage>();
            foreach (var item in items)
            {
                var attachments = new List<AgentAttachment>();
                foreach (var path in item.Attachments)
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    try
                    {
                        var parsed = AttachmentService.Parse(path);
                        if (parsed.Kind == AttachmentKind.Document &&
                            parsed.Text.StartsWith("(暂不支持解析", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        attachments.Add(new AgentAttachment
                        {
                            Name = parsed.Name,
                            Path = path,
                            Kind = parsed.Kind,
                            MediaType = parsed.MediaType,
                            ImageBytes = parsed.ImageBytes,
                            Text = parsed.Text
                        });
                    }
                    catch
                    {
                        // 单个附件恢复失败不影响其他排队消息。
                    }
                }

                if (string.IsNullOrWhiteSpace(item.Text) && attachments.Count == 0)
                {
                    continue;
                }

                result.Add(new QueuedAgentMessage(item.Text, isSteering: false, attachments));
            }

            return result;
        }
        catch
        {
            return Array.Empty<QueuedAgentMessage>();
        }
    }

    public static void Save(long sessionId, IEnumerable<QueuedAgentMessage> messages)
    {
        if (sessionId <= 0)
        {
            return;
        }

        var items = messages
            .Where(m => !m.Cancelled)
            .Select(m => new PersistedQueueItem
            {
                Text = m.Text,
                Attachments = m.Attachments.Select(a => a.Path).ToArray()
            })
            .ToList();

        if (items.Count == 0)
        {
            Delete(sessionId);
            return;
        }

        try
        {
            Directory.CreateDirectory(Dir);
            var file = PathFor(sessionId);
            var temp = file + ".tmp";
            File.WriteAllText(temp, Protector.Protect(JsonSerializer.Serialize(items)) ?? string.Empty);
            File.Move(temp, file, overwrite: true);
        }
        catch
        {
            // 队列持久化失败不影响当前会话。
        }
    }

    public static void Delete(long sessionId)
    {
        if (sessionId <= 0)
        {
            return;
        }

        try
        {
            var file = PathFor(sessionId);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch
        {
        }
    }

    private static string PathFor(long sessionId) => Path.Combine(Dir, $"q-{sessionId}.json");
}
