using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Trigger;

public sealed class WebhookSendResult
{
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public string Error { get; set; } = string.Empty;
}

/// <summary>webhook 执行结果通知文案（钉钉/企微/飞书/通用 payload 共用 {{notice}}）。</summary>
public static class TriggerNotice
{
    private static readonly Dictionary<TriggerRunCause, string> CauseText = new()
    {
        [TriggerRunCause.Manual] = "手动",
        [TriggerRunCause.Schedule] = "定时",
        [TriggerRunCause.Sync] = "同步联动"
    };

    public static string CauseLabel(TriggerRunCause cause)
        => CauseText.TryGetValue(cause, out var text) ? text : cause.ToString();

    public static string ShortType(TriggerActionType type) => type switch
    {
        TriggerActionType.GitCommitPush => "git",
        TriggerActionType.Webhook => "webhook",
        TriggerActionType.Backup => "备份",
        TriggerActionType.DatabaseBackup => "数据库备份",
        _ => type.ToString()
    };

    /// <summary>构建通知正文（已含步骤明细）。</summary>
    public static string Build(TriggerConfig trigger, TriggerRunCause cause, bool success,
        IReadOnlyList<TriggerActionResult> steps, DateTime at)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"触发事件: {trigger.Name}");
        sb.AppendLine($"触发: {CauseLabel(cause)}");
        sb.AppendLine($"结果: {(success ? "成功" : "失败")}");
        sb.AppendLine($"时间: {at:yyyy-MM-dd HH:mm:ss}");
        if (steps.Count > 0)
        {
            sb.AppendLine("步骤:");
            for (var i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                sb.AppendLine($"  {i + 1}. [{(s.Success ? "✓" : "✗")}] {ShortType(s.Type)}（{s.Duration.TotalSeconds:F1}s）");
                var detail = s.Success ? s.Output : s.Error;
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    foreach (var line in detail.Split('\n'))
                    {
                        var trimmed = line.TrimEnd('\r');
                        if (trimmed.Length > 300) trimmed = trimmed[..300] + "…";
                        sb.AppendLine($"     {trimmed}");
                    }
                }
            }
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// webhook 通知发送：按模板构造 JSON 载荷（{{变量}} 渲染），支持自定义头与钉钉/飞书签名。
/// 请求体用 JSON 序列化构造（钉钉/企微/飞书），Generic 模板按用户字符串渲染（用户对 JSON 正确性负责）。
/// </summary>
public static class WebhookSender
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };

    public static async Task<WebhookSendResult> SendAsync(TriggerActionContext ctx, CancellationToken ct = default)
    {
        var action = ctx.Action;
        var vars = ctx.Vars;
        var result = new WebhookSendResult();

        try
        {
            var url = action.Url;
            string body;

            switch (action.WebhookTemplate)
            {
                case TriggerWebhookTemplate.DingTalk:
                {
                    body = JsonSerializer.Serialize(new
                    {
                        msgtype = "markdown",
                        markdown = new
                        {
                            title = TriggerTemplate.Render("{{trigger}}", vars),
                            text = "# " + TriggerTemplate.Render("{{trigger}}", vars) + "\n" + TriggerTemplate.Render("{{notice}}", vars)
                        }
                    });
                    if (!string.IsNullOrEmpty(action.Secret))
                    {
                        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                        var sign = Sign(ts, action.Secret);
                        url += (url.Contains('?') ? '&' : '?') + $"timestamp={ts}&sign={Uri.EscapeDataString(sign)}";
                    }
                    break;
                }
                case TriggerWebhookTemplate.WeCom:
                {
                    body = JsonSerializer.Serialize(new
                    {
                        msgtype = "markdown",
                        markdown = new { content = TriggerTemplate.Render("{{notice}}", vars) }
                    });
                    break;
                }
                case TriggerWebhookTemplate.Feishu:
                {
                    var payload = new Dictionary<string, object?>
                    {
                        ["msgtype"] = "text",
                        ["content"] = new Dictionary<string, object?>
                        {
                            ["text"] = TriggerTemplate.Render("{{notice}}", vars)
                        }
                    };
                    if (!string.IsNullOrEmpty(action.Secret))
                    {
                        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                        payload["timestamp"] = ts;
                        payload["sign"] = Sign(ts, action.Secret!);
                    }
                    body = JsonSerializer.Serialize(payload);
                    break;
                }
                default:
                {
                    var template = string.IsNullOrWhiteSpace(action.BodyTemplate)
                        ? "{\"trigger\":\"{{trigger}}\",\"result\":\"{{result}}\",\"cause\":\"{{cause}}\",\"timestamp\":\"{{timestamp}}\",\"detail\":\"{{steps}}\"}"
                        : action.BodyTemplate;
                    body = TriggerTemplate.Render(template, vars);
                    break;
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            foreach (var (key, value) in action.Headers)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;
                if (!request.Headers.TryAddWithoutValidation(key, value))
                    request.Content.Headers.TryAddWithoutValidation(key, value);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, action.TimeoutSeconds)));

            using var response = await Http.SendAsync(request, cts.Token);
            result.StatusCode = (int)response.StatusCode;
            result.Success = response.IsSuccessStatusCode;
            if (!result.Success)
            {
                var text = await response.Content.ReadAsStringAsync(cts.Token);
                if (text.Length > 300) text = text[..300] + "…";
                result.Error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}" +
                               (string.IsNullOrWhiteSpace(text) ? string.Empty : $": {text}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result.Error = "已取消";
        }
        catch (OperationCanceledException)
        {
            result.Error = "webhook 请求超时";
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>钉钉/飞书签名：Base64(HMAC-SHA256(key=secret, data="{timestamp}\n{secret}"))。</summary>
    public static string Sign(string timestamp, string secret)
    {
        var data = Encoding.UTF8.GetBytes(timestamp + "\n" + secret);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(data));
    }
}
