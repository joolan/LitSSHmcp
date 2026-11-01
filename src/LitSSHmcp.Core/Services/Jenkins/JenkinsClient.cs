using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Jenkins;

public class JenkinsApiException : Exception
{
    public JenkinsApiException(string message, HttpStatusCode? statusCode = null, string? responseBody = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode? StatusCode { get; }
    public string? ResponseBody { get; }
}

public sealed class JenkinsAuthenticationException : JenkinsApiException
{
    public JenkinsAuthenticationException(string message, HttpStatusCode? statusCode = null, string? responseBody = null)
        : base(message, statusCode, responseBody)
    {
    }
}

public sealed record JenkinsConnectionInfo(string Url, string Version);

public sealed class JenkinsJob
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string Color { get; init; } = string.Empty;
    public bool IsFolder { get; init; }
    public bool Buildable { get; init; }
}

public sealed record JenkinsView(string Name, string Url, string Description)
{
    public bool IsAll => string.IsNullOrWhiteSpace(Url);
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "未命名视图" : Name;
}

public sealed record JenkinsParameterDefinition(
    string Name,
    string Type,
    string Description,
    string DefaultValue,
    string[] Choices);

public sealed class JenkinsJobDetail
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Color { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public bool Buildable { get; init; } = true;
    public IReadOnlyList<JenkinsParameterDefinition> Parameters { get; init; } = Array.Empty<JenkinsParameterDefinition>();
    public IReadOnlyList<JenkinsBuildSummary> Builds { get; init; } = Array.Empty<JenkinsBuildSummary>();
}

public sealed record JenkinsBuildSummary(
    int Number,
    string Url,
    string Result,
    bool Building,
    DateTimeOffset? Timestamp,
    long Duration)
{
    public string Display => Building
        ? $"#{Number} 运行中"
        : $"#{Number} {StatusText}";

    public string StatusText => Building ? "运行中" : Result switch
    {
        "SUCCESS" => "成功",
        "FAILURE" => "失败",
        "ABORTED" => "已中止",
        "UNSTABLE" => "不稳定",
        "NOT_BUILT" => "未构建",
        _ => string.IsNullOrWhiteSpace(Result) ? "未知" : Result
    };

    public string TimestampText => Timestamp is { } timestamp
        ? timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "—";

    public string DurationText => Duration > 0
        ? FormatDuration(Duration)
        : "—";

    private static string FormatDuration(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : span.TotalMinutes >= 1
                ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
                : $"{Math.Max(1, span.Seconds)}s";
    }
}

public sealed record JenkinsBuildStatus(
    int Number,
    string Url,
    bool Building,
    string Result,
    DateTimeOffset? Timestamp,
    long Duration,
    long EstimatedDuration);

public sealed record JenkinsBuildReference(int Number, string Url);

public sealed record JenkinsQueueItem(
    long Id,
    string Why,
    bool Cancelled,
    JenkinsBuildReference? Executable);

public sealed record JenkinsQueueEntry(
    long Id,
    string TaskName,
    string TaskUrl,
    string Why,
    bool Blocked,
    bool Stuck,
    bool Buildable,
    bool Cancelled,
    DateTimeOffset? InQueueSince,
    JenkinsBuildReference? Executable)
{
    public string Display => string.IsNullOrWhiteSpace(TaskName) ? $"队列 #{Id}" : TaskName;
    public string StateText => Executable is { } build
        ? $"已开始 #{build.Number}"
        : Cancelled ? "已取消"
        : Blocked ? "阻塞"
        : Stuck ? "卡住"
        : string.IsNullOrWhiteSpace(Why) ? "等待中" : Why;
}

public sealed record JenkinsTriggerResult(long? QueueId, string? Location);

public sealed record JenkinsLogChunk(string Text, long NextStart, bool MoreData);

public sealed record JenkinsArtifact(string FileName, string RelativePath, string Url);

public sealed record JenkinsStage(string Name, string Status, long DurationMilliseconds)
{
    public string StatusText => Status switch
    {
        "SUCCESS" => "成功",
        "FAILED" => "失败",
        "UNSTABLE" => "不稳定",
        "ABORTED" => "已中止",
        "IN_PROGRESS" => "进行中",
        "PAUSED_PENDING_INPUT" => "等待输入",
        _ => string.IsNullOrWhiteSpace(Status) ? "未知" : Status
    };

    public string DurationText => DurationMilliseconds > 0
        ? TimeSpan.FromMilliseconds(DurationMilliseconds).TotalMinutes >= 1
            ? $"{(int)TimeSpan.FromMilliseconds(DurationMilliseconds).TotalMinutes}m {TimeSpan.FromMilliseconds(DurationMilliseconds).Seconds}s"
            : $"{Math.Max(1, TimeSpan.FromMilliseconds(DurationMilliseconds).Seconds)}s"
        : "—";
}

public sealed record JenkinsTestFailure(string ClassName, string Name, string Status, string ErrorDetails);

public sealed record JenkinsTestReport(
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<JenkinsTestFailure> Failures);

public sealed record JenkinsBuildExtras(
    IReadOnlyList<JenkinsArtifact> Artifacts,
    IReadOnlyList<JenkinsStage> Stages,
    JenkinsTestReport? TestReport);

/// <summary>
/// Jenkins 2.x Remote API 客户端。只负责 HTTP/认证/CSRF/JSON 解析；
/// 多连接隔离、只读策略和确认交互由上层负责。
/// </summary>
public sealed class JenkinsClient : IDisposable
{
    private const string UserAgent = "LitSSHmcp/1.5";
    private const int MaxErrorBody = 400;

    private readonly HttpClient _http;
    private readonly JenkinsSshTunnel? _tunnel;
    private readonly Uri _baseUri;
    private readonly SemaphoreSlim _crumbGate = new(1, 1);
    private string? _crumb;
    private string? _crumbField;
    private bool _crumbChecked;
    private bool _disposed;

    public JenkinsClient(
        JenkinsConnectionConfig config,
        HttpMessageHandler? handler = null,
        JenkinsSshTunnel? tunnel = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _tunnel = tunnel;

        if (!Uri.TryCreate(config.BaseUrl.Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Jenkins 地址必须是有效的 http/https URL。", nameof(config));

        _baseUri = new Uri(baseUri.ToString().TrimEnd('/') + "/");

        var httpHandler = handler ?? CreateHandler(config, tunnel);

        _http = new HttpClient(httpHandler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 5, 300))
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        if (!string.IsNullOrWhiteSpace(config.Username) || !string.IsNullOrWhiteSpace(config.ApiToken))
        {
            var credential = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{config.Username}:{config.ApiToken}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credential);
        }
    }

    private static SocketsHttpHandler CreateHandler(JenkinsConnectionConfig config, JenkinsSshTunnel? tunnel)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false,
            UseProxy = tunnel is null
        };

        if (config.IgnoreTlsErrors)
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;

        if (tunnel is not null)
        {
            handler.ConnectCallback = (context, ct) => Socks5Connector.ConnectAsync(
                "127.0.0.1",
                tunnel.LocalPort,
                context.DnsEndPoint.Host,
                context.DnsEndPoint.Port,
                ct);
        }

        return handler;
    }

    public Uri BaseUri => _baseUri;

    public async Task<JenkinsConnectionInfo> TestConnectionAsync(CancellationToken ct = default)
    {
        using var response = await SendCoreAsync(
            HttpMethod.Get,
            BuildUri("api/json?tree=mode,nodeDescription"),
            contentFactory: null,
            allowCrumbRetry: false,
            ct).ConfigureAwait(false);

        var text = await ReadBodyAsync(response, ct).ConfigureAwait(false);
        EnsureNotLoginPage(text, response.StatusCode);
        EnsureSuccess(response, text);

        var version = response.Headers.TryGetValues("X-Jenkins", out var values)
            ? values.FirstOrDefault() ?? string.Empty
            : string.Empty;
        return new JenkinsConnectionInfo(_baseUri.ToString().TrimEnd('/'), version);
    }

    /// <summary>列出根任务或指定文件夹的一级子任务；文件夹由 UI 展开时继续调用。</summary>
    public async Task<IReadOnlyList<JenkinsJob>> ListJobsAsync(string? folderUrl = null, CancellationToken ct = default)
    {
        var parent = string.IsNullOrWhiteSpace(folderUrl)
            ? _baseUri
            : new Uri(EnsureTrailingSlash(folderUrl));
        return await ListJobsAtAsync(parent, ct).ConfigureAwait(false);
    }

    /// <summary>列出 Jenkins View；View 常用于分类/分组，任务可不在根任务列表中。</summary>
    public async Task<IReadOnlyList<JenkinsView>> ListViewsAsync(CancellationToken ct = default)
    {
        var uri = new Uri(_baseUri, "api/json?tree=views[name,url,description]");
        using var document = await GetJsonAsync(uri, ct).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("views", out var views) || views.ValueKind != JsonValueKind.Array)
            return Array.Empty<JenkinsView>();

        var result = new List<JenkinsView>();
        foreach (var view in views.EnumerateArray())
        {
            if (view.ValueKind != JsonValueKind.Object)
                continue;
            var url = ResolveUrl(GetString(view, "url"), _baseUri).ToString();
            result.Add(new JenkinsView(GetString(view, "name"), url, GetString(view, "description")));
        }

        return result;
    }

    public async Task<IReadOnlyList<JenkinsJob>> ListJobsInViewAsync(string viewUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(viewUrl))
            return await ListJobsAsync(ct: ct).ConfigureAwait(false);

        var parent = new Uri(EnsureTrailingSlash(ResolveUrl(viewUrl, _baseUri).ToString()));
        return await ListJobsAtAsync(parent, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<JenkinsJob>> ListJobsAtAsync(Uri parent, CancellationToken ct)
    {
        var uri = new Uri(parent, "api/json?tree=jobs[name,url,color,_class,buildable]");
        using var document = await GetJsonAsync(uri, ct).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
            return Array.Empty<JenkinsJob>();

        var result = new List<JenkinsJob>();
        foreach (var job in jobs.EnumerateArray())
            result.Add(ParseJob(job));
        return result;
    }

    public async Task<JenkinsJobDetail> GetJobAsync(string jobUrl, CancellationToken ct = default)
    {
        var uri = new Uri(new Uri(EnsureTrailingSlash(jobUrl)),
            "api/json?tree=name,url,color,_class,description,buildable,property[parameterDefinitions[name,type,description,defaultValue,choices]],builds[number,url,result,building,timestamp,duration,displayName]");
        using var document = await GetJsonAsync(uri, ct).ConfigureAwait(false);
        var root = document.RootElement;

        var name = GetString(root, "name");
        var url = ResolveUrl(GetString(root, "url"), _baseUri).ToString();
        var buildable = GetBool(root, "buildable", true);
        var parameters = ParseParameters(root);
        var builds = ParseBuilds(root);
        return new JenkinsJobDetail
        {
            Name = name,
            Path = GetJobPath(url),
            Url = url,
            Description = GetString(root, "description"),
            Color = GetString(root, "color"),
            ClassName = GetString(root, "_class"),
            Buildable = buildable,
            Parameters = parameters,
            Builds = builds
        };
    }

    public async Task<JenkinsTriggerResult> TriggerBuildAsync(
        string jobUrl,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken ct = default)
    {
        var useParameters = parameters is { Count: > 0 };
        var endpoint = useParameters ? "buildWithParameters" : "build";
        var uri = new Uri(new Uri(EnsureTrailingSlash(jobUrl)), endpoint);

        Func<HttpContent?> contentFactory = useParameters
            ? () => new FormUrlEncodedContent(parameters!)
            : () => null;

        using var response = await SendCoreAsync(
            HttpMethod.Post,
            uri,
            contentFactory,
            allowCrumbRetry: true,
            ct).ConfigureAwait(false);

        var text = await ReadBodyAsync(response, ct).ConfigureAwait(false);
        EnsureSuccess(response, text, allowRedirect: true);

        var location = response.Headers.Location?.ToString();
        return new JenkinsTriggerResult(ParseQueueId(location), location);
    }

    public async Task<IReadOnlyList<JenkinsQueueEntry>> ListQueueItemsAsync(CancellationToken ct = default)
    {
        var uri = new Uri(_baseUri,
            "queue/api/json?tree=items[id,why,blocked,stuck,buildable,cancelled,inQueueSince,executable[number,url],task[name,url,color]]");
        using var document = await GetJsonAsync(uri, ct).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return Array.Empty<JenkinsQueueEntry>();

        var result = new List<JenkinsQueueEntry>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var task = item.TryGetProperty("task", out var taskElement) && taskElement.ValueKind == JsonValueKind.Object
                ? taskElement
                : default;
            var executable = item.TryGetProperty("executable", out var executableElement) &&
                             executableElement.ValueKind == JsonValueKind.Object
                ? new JenkinsBuildReference(
                    (int)GetLong(executableElement, "number"),
                    ResolveUrl(GetString(executableElement, "url"), _baseUri).ToString())
                : null;

            result.Add(new JenkinsQueueEntry(
                GetLong(item, "id"),
                task.ValueKind == JsonValueKind.Object ? GetString(task, "name") : string.Empty,
                task.ValueKind == JsonValueKind.Object
                    ? ResolveUrl(GetString(task, "url"), _baseUri).ToString()
                    : string.Empty,
                GetString(item, "why"),
                GetBool(item, "blocked", false),
                GetBool(item, "stuck", false),
                GetBool(item, "buildable", false),
                GetBool(item, "cancelled", false),
                GetTimestamp(item, "inQueueSince"),
                executable));
        }

        return result;
    }

    public async Task<JenkinsQueueItem> GetQueueItemAsync(long queueId, CancellationToken ct = default)
    {
        var uri = new Uri(_baseUri,
            $"queue/item/{queueId.ToString(CultureInfo.InvariantCulture)}/api/json?tree=id,why,cancelled,executable[number,url]");
        using var document = await GetJsonAsync(uri, ct).ConfigureAwait(false);
        var root = document.RootElement;
        var executable = root.TryGetProperty("executable", out var executableElement) &&
                         executableElement.ValueKind == JsonValueKind.Object
            ? new JenkinsBuildReference(
                (int)GetLong(executableElement, "number"),
                ResolveUrl(GetString(executableElement, "url"), _baseUri).ToString())
            : null;

        return new JenkinsQueueItem(
            GetLong(root, "id"),
            GetString(root, "why"),
            GetBool(root, "cancelled", false),
            executable);
    }

    public async Task<JenkinsBuildStatus> GetBuildAsync(
        string jobUrl,
        int buildNumber,
        CancellationToken ct = default)
    {
        var uri = new Uri(new Uri(EnsureTrailingSlash(jobUrl)),
            $"{buildNumber.ToString(CultureInfo.InvariantCulture)}/api/json?tree=number,url,building,result,duration,estimatedDuration,timestamp");
        using var document = await GetJsonAsync(uri, ct).ConfigureAwait(false);
        var root = document.RootElement;
        return new JenkinsBuildStatus(
            (int)GetLong(root, "number"),
            ResolveUrl(GetString(root, "url"), _baseUri).ToString(),
            GetBool(root, "building", false),
            GetString(root, "result"),
            GetTimestamp(root),
            GetLong(root, "duration"),
            GetLong(root, "estimatedDuration"));
    }

    public async Task<JenkinsBuildExtras> GetBuildExtrasAsync(
        string jobUrl,
        int buildNumber,
        CancellationToken ct = default)
    {
        var buildUri = new Uri(new Uri(EnsureTrailingSlash(jobUrl)),
            $"{buildNumber.ToString(CultureInfo.InvariantCulture)}/");
        var artifacts = new List<JenkinsArtifact>();

        using (var artifactDocument = await TryGetJsonAsync(
                   new Uri(buildUri, "api/json?tree=artifacts[fileName,relativePath]"), ct).ConfigureAwait(false))
        {
            if (artifactDocument is not null &&
                artifactDocument.RootElement.TryGetProperty("artifacts", out var artifactArray) &&
                artifactArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var artifact in artifactArray.EnumerateArray())
                {
                    var relativePath = GetString(artifact, "relativePath");
                    if (relativePath.Length == 0)
                        continue;
                    artifacts.Add(new JenkinsArtifact(
                        GetString(artifact, "fileName"),
                        relativePath,
                        new Uri(buildUri, "artifact/" + relativePath).ToString()));
                }
            }
        }

        JenkinsTestReport? testReport = null;
        using (var testDocument = await TryGetJsonAsync(
                   new Uri(buildUri, "testReport/api/json?tree=totalCount,failCount,skipCount,passCount,suites[cases[className,name,status,duration,errorDetails]]"), ct)
                   .ConfigureAwait(false))
        {
            if (testDocument is not null)
                testReport = ParseTestReport(testDocument.RootElement);
        }

        var stages = new List<JenkinsStage>();
        using (var stageDocument = await TryGetJsonAsync(new Uri(buildUri, "wfapi/describe"), ct)
                   .ConfigureAwait(false))
        {
            if (stageDocument is not null &&
                stageDocument.RootElement.TryGetProperty("stages", out var stageArray) &&
                stageArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var stage in stageArray.EnumerateArray())
                {
                    stages.Add(new JenkinsStage(
                        GetString(stage, "name"),
                        GetString(stage, "status"),
                        GetLong(stage, "durationMillis")));
                }
            }
        }

        return new JenkinsBuildExtras(artifacts, stages, testReport);
    }

    private async Task<JsonDocument?> TryGetJsonAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            return await GetJsonAsync(uri, ct).ConfigureAwait(false);
        }
        catch (JenkinsApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return null;
        }
    }

    private static JenkinsTestReport ParseTestReport(JsonElement root)
    {
        var total = (int)GetLong(root, "totalCount");
        var failed = (int)GetLong(root, "failCount");
        var skipped = (int)GetLong(root, "skipCount");
        var passed = (int)GetLong(root, "passCount");
        if (passed == 0 && total > 0)
            passed = Math.Max(0, total - failed - skipped);

        var failures = new List<JenkinsTestFailure>();
        if (root.TryGetProperty("suites", out var suites) && suites.ValueKind == JsonValueKind.Array)
        {
            foreach (var suite in suites.EnumerateArray())
            {
                if (!suite.TryGetProperty("cases", out var cases) || cases.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var testCase in cases.EnumerateArray())
                {
                    var status = GetString(testCase, "status");
                    if (status is "PASSED" or "FIXED" or "SKIPPED")
                        continue;

                    failures.Add(new JenkinsTestFailure(
                        GetString(testCase, "className"),
                        GetString(testCase, "name"),
                        status,
                        GetString(testCase, "errorDetails")));
                    if (failures.Count >= 50)
                        break;
                }
            }
        }

        return new JenkinsTestReport(total, passed, failed, skipped, failures);
    }

    public async Task<string> GetBuildLogAsync(
        string jobUrl,
        int buildNumber,
        CancellationToken ct = default)
    {
        var uri = new Uri(new Uri(EnsureTrailingSlash(jobUrl)),
            $"{buildNumber.ToString(CultureInfo.InvariantCulture)}/consoleText");
        using var response = await SendCoreAsync(
            HttpMethod.Get,
            uri,
            contentFactory: null,
            allowCrumbRetry: false,
            ct).ConfigureAwait(false);
        var text = await ReadBodyAsync(response, ct).ConfigureAwait(false);
        EnsureNotLoginPage(text, response.StatusCode);
        EnsureSuccess(response, text);
        return text;
    }

    /// <summary>
    /// 读取增量日志。Jenkins 的 progressiveText 返回本次文本和 X-Text-Size；
    /// X-More-Data 为 true 表示还可以继续读取。
    /// </summary>
    public async Task<JenkinsLogChunk> GetBuildLogChunkAsync(
        string jobUrl,
        int buildNumber,
        long start,
        CancellationToken ct = default)
    {
        var uri = new Uri(new Uri(EnsureTrailingSlash(jobUrl)),
            $"{buildNumber.ToString(CultureInfo.InvariantCulture)}/logText/progressiveText?start={Math.Max(0, start).ToString(CultureInfo.InvariantCulture)}");
        using var response = await SendCoreAsync(
            HttpMethod.Get,
            uri,
            contentFactory: null,
            allowCrumbRetry: false,
            ct).ConfigureAwait(false);
        var text = await ReadBodyAsync(response, ct).ConfigureAwait(false);
        EnsureNotLoginPage(text, response.StatusCode);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var fullLog = await GetBuildLogAsync(jobUrl, buildNumber, ct).ConfigureAwait(false);
            return new JenkinsLogChunk(fullLog, Encoding.UTF8.GetByteCount(fullLog), false);
        }

        EnsureSuccess(response, text);

        var nextStart = response.Headers.TryGetValues("X-Text-Size", out var sizes) &&
                        long.TryParse(sizes.FirstOrDefault(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var parsedSize)
            ? parsedSize
            : start + Encoding.UTF8.GetByteCount(text);
        var moreData = response.Headers.TryGetValues("X-More-Data", out var moreValues) &&
                       bool.TryParse(moreValues.FirstOrDefault(), out var parsedMore) &&
                       parsedMore;
        return new JenkinsLogChunk(text, nextStart, moreData);
    }

    public async Task StopBuildAsync(string jobUrl, int buildNumber, CancellationToken ct = default)
    {
        var uri = new Uri(new Uri(EnsureTrailingSlash(jobUrl)),
            $"{buildNumber.ToString(CultureInfo.InvariantCulture)}/stop");
        using var response = await SendCoreAsync(
            HttpMethod.Post,
            uri,
            contentFactory: null,
            allowCrumbRetry: true,
            ct).ConfigureAwait(false);
        var text = await ReadBodyAsync(response, ct).ConfigureAwait(false);
        EnsureSuccess(response, text, allowRedirect: true);
    }

    public async Task CancelQueueItemAsync(long queueId, CancellationToken ct = default)
    {
        var uri = new Uri(_baseUri,
            $"queue/cancelItem?id={queueId.ToString(CultureInfo.InvariantCulture)}");
        using var response = await SendCoreAsync(
            HttpMethod.Post,
            uri,
            contentFactory: null,
            allowCrumbRetry: true,
            ct).ConfigureAwait(false);
        var text = await ReadBodyAsync(response, ct).ConfigureAwait(false);
        EnsureSuccess(response, text, allowRedirect: true);
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken ct)
    {
        using var response = await SendCoreAsync(
            HttpMethod.Get,
            uri,
            contentFactory: null,
            allowCrumbRetry: false,
            ct).ConfigureAwait(false);
        var text = await ReadBodyAsync(response, ct).ConfigureAwait(false);
        EnsureNotLoginPage(text, response.StatusCode);
        EnsureSuccess(response, text);
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new JenkinsApiException("Jenkins 返回了无法解析的 JSON。", response.StatusCode, Clip(text), ex);
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method,
        Uri uri,
        Func<HttpContent?>? contentFactory,
        bool allowCrumbRetry,
        CancellationToken ct,
        int redirectCount = 0)
    {
        ThrowIfDisposed();

        var crumb = method == HttpMethod.Post ? await GetCrumbAsync(ct).ConfigureAwait(false) : null;
        var response = await SendOnceAsync(method, uri, contentFactory, crumb, ct).ConfigureAwait(false);

        if (allowCrumbRetry && method == HttpMethod.Post && response.StatusCode == HttpStatusCode.Forbidden)
        {
            response.Dispose();
            ResetCrumb();
            crumb = await GetCrumbAsync(ct).ConfigureAwait(false);
            response = await SendOnceAsync(method, uri, contentFactory, crumb, ct).ConfigureAwait(false);
        }

        if (method == HttpMethod.Get && redirectCount < 3 && response.Headers.Location is { } location &&
            response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
            HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
            response.Dispose();
            return await SendCoreAsync(method, next, contentFactory: null, allowCrumbRetry, ct, redirectCount + 1)
                .ConfigureAwait(false);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
            response.Dispose();
            throw new JenkinsAuthenticationException("Jenkins 认证失败，请检查用户名和 API Token。",
                HttpStatusCode.Unauthorized, Clip(body));
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method,
        Uri uri,
        Func<HttpContent?>? contentFactory,
        (string Field, string Value)? crumb,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (contentFactory is not null)
        {
            var content = contentFactory();
            if (content is not null)
                request.Content = content;
        }

        if (crumb is { } c)
            request.Headers.TryAddWithoutValidation(c.Field, c.Value);

        return await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
    }

    private async Task<(string Field, string Value)?> GetCrumbAsync(CancellationToken ct)
    {
        if (_crumbChecked)
            return _crumb is null || _crumbField is null ? null : (_crumbField, _crumb);

        await _crumbGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_crumbChecked)
                return _crumb is null || _crumbField is null ? null : (_crumbField, _crumb);

            var uri = new Uri(_baseUri, "crumbIssuer/api/json");
            using var response = await SendOnceAsync(HttpMethod.Get, uri, null, null, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _crumbChecked = true;
                return null;
            }

            var text = await ReadBodyAsync(response, ct).ConfigureAwait(false);
            EnsureNotLoginPage(text, response.StatusCode);
            EnsureSuccess(response, text);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            _crumb = GetString(root, "crumb");
            _crumbField = GetString(root, "crumbRequestField");
            _crumbChecked = _crumb.Length > 0 && _crumbField.Length > 0;
            return _crumbChecked ? (_crumbField!, _crumb!) : null;
        }
        catch (JenkinsAuthenticationException)
        {
            throw;
        }
        catch (JenkinsApiException)
        {
            throw;
        }
        finally
        {
            _crumbGate.Release();
        }
    }

    private void ResetCrumb()
    {
        _crumb = null;
        _crumbField = null;
        _crumbChecked = false;
    }

    private Uri BuildUri(string relative)
        => new(_baseUri, relative);

    private JenkinsJob ParseJob(JsonElement element)
    {
        var className = GetString(element, "_class");
        var url = GetString(element, "url");
        var name = GetString(element, "name");
        return new JenkinsJob
        {
            Name = string.IsNullOrWhiteSpace(name) ? GetJobPath(url) : name,
            Path = GetJobPath(url),
            Url = ResolveUrl(url, _baseUri).ToString(),
            ClassName = className,
            Color = GetString(element, "color"),
            IsFolder = IsFolderClass(className),
            Buildable = GetBool(element, "buildable", !IsFolderClass(className))
        };
    }

    private static bool IsFolderClass(string className)
        => className.Contains("Folder", StringComparison.OrdinalIgnoreCase) ||
           className.Contains("MultiBranchProject", StringComparison.OrdinalIgnoreCase) ||
           className.Contains("OrganizationFolder", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<JenkinsParameterDefinition> ParseParameters(JsonElement root)
    {
        if (!root.TryGetProperty("property", out var properties) || properties.ValueKind != JsonValueKind.Array)
            return Array.Empty<JenkinsParameterDefinition>();

        var result = new List<JenkinsParameterDefinition>();
        foreach (var property in properties.EnumerateArray())
        {
            if (!property.TryGetProperty("parameterDefinitions", out var definitions) ||
                definitions.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var definition in definitions.EnumerateArray())
            {
                var choices = definition.TryGetProperty("choices", out var choiceElement) &&
                              choiceElement.ValueKind == JsonValueKind.Array
                    ? choiceElement.EnumerateArray().Select(choice => choice.ToString()).ToArray()
                    : Array.Empty<string>();

                result.Add(new JenkinsParameterDefinition(
                    GetString(definition, "name"),
                    GetString(definition, "type"),
                    GetString(definition, "description"),
                    GetString(definition, "defaultValue"),
                    choices));
            }
        }

        return result;
    }

    private IReadOnlyList<JenkinsBuildSummary> ParseBuilds(JsonElement root)
    {
        if (!root.TryGetProperty("builds", out var builds) || builds.ValueKind != JsonValueKind.Array)
            return Array.Empty<JenkinsBuildSummary>();

        return builds.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new JenkinsBuildSummary(
                (int)GetLong(item, "number"),
                ResolveUrl(GetString(item, "url"), _baseUri).ToString(),
                GetString(item, "result"),
                GetBool(item, "building", false),
                GetTimestamp(item),
                GetLong(item, "duration")))
            .ToArray();
    }

    private static string GetJobPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url.Trim('/');

        var path = uri.AbsolutePath;
        var jobMarker = path.IndexOf("/job/", StringComparison.Ordinal);
        if (jobMarker >= 0)
            path = path[(jobMarker + 5)..];

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !string.Equals(segment, "job", StringComparison.OrdinalIgnoreCase))
            .Select(Uri.UnescapeDataString)
            .ToArray();
        return string.Join("/", segments);
    }

    private static Uri ResolveUrl(string value, Uri baseUri)
        => Uri.TryCreate(value, UriKind.Absolute, out var absolute) ? absolute : new Uri(baseUri, value);

    private static string EnsureTrailingSlash(string url)
        => url.EndsWith("/", StringComparison.Ordinal) ? url : url + "/";

    private static long? ParseQueueId(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return null;

        var trimmed = location.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var idText = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
    }

    private static void EnsureNotLoginPage(string text, HttpStatusCode status)
    {
        if (text.TrimStart().StartsWith("<", StringComparison.Ordinal))
        {
            throw new JenkinsAuthenticationException(
                "Jenkins 返回了 HTML 登录页，请检查 API Token、反向代理认证或匿名访问权限。",
                status,
                Clip(text));
        }
    }

    private static void EnsureSuccess(
        HttpResponseMessage response,
        string text,
        bool allowRedirect = false)
    {
        if (response.IsSuccessStatusCode)
            return;

        if (allowRedirect && response.StatusCode is
            HttpStatusCode.MovedPermanently or
            HttpStatusCode.Redirect or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect)
            return;

        var message = response.StatusCode switch
        {
            HttpStatusCode.Forbidden => "Jenkins 拒绝访问：权限不足或 CSRF 校验失败。",
            HttpStatusCode.NotFound => "Jenkins 接口不存在或任务已被删除。",
            _ => $"Jenkins 请求失败（HTTP {(int)response.StatusCode}）。"
        };
        throw new JenkinsApiException(message, response.StatusCode, Clip(text));
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
        => await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

    private static string Clip(string text)
    {
        var singleLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= MaxErrorBody ? singleLine : singleLine[..MaxErrorBody] + "...";
    }

    private static string GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString()
        };
    }

    private static bool GetBool(JsonElement element, string property, bool defaultValue)
    {
        if (!element.TryGetProperty(property, out var value))
            return defaultValue;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        return bool.TryParse(value.ToString(), out var result) ? result : defaultValue;
    }

    private static long GetLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static DateTimeOffset? GetTimestamp(JsonElement element, string property = "timestamp")
    {
        var milliseconds = GetLong(element, property);
        if (milliseconds <= 0)
            return null;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http.Dispose();
        _tunnel?.Dispose();
        _crumbGate.Dispose();
    }
}
