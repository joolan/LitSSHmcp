using System.Net;
using System.Net.Http;
using System.Text;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Jenkins;
using Xunit;

namespace LitSSHmcp.Tests;

public class JenkinsClientTests
{
    [Fact]
    public async Task Lists_folders_and_jobs_with_normalized_paths()
    {
        using var handler = new StubHandler((_, _) => JsonResponse(
            """
            {
              "jobs": [
                { "name": "team", "url": "https://jenkins.example/ci/job/team/", "_class": "com.cloudbees.hudson.plugins.folder.Folder", "buildable": false },
                { "name": "app", "url": "https://jenkins.example/ci/job/app/", "_class": "org.jenkinsci.plugins.workflow.job.WorkflowJob", "buildable": true }
              ]
            }
            """));
        using var client = CreateClient(handler, "https://jenkins.example/ci");

        var jobs = await client.ListJobsAsync();

        Assert.Equal(2, jobs.Count);
        Assert.True(jobs[0].IsFolder);
        Assert.Equal("team", jobs[0].Path);
        Assert.False(jobs[1].IsFolder);
        Assert.True(jobs[1].Buildable);
        Assert.Equal("https://jenkins.example/ci/job/app/", jobs[1].Url);
        Assert.Equal("/ci/api/json", handler.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Reads_parameters_and_build_history()
    {
        using var handler = new StubHandler((_, _) => JsonResponse(
            """
            {
              "name": "deploy",
              "url": "https://jenkins.example/job/deploy/",
              "buildable": true,
              "property": [
                {
                  "parameterDefinitions": [
                    { "name": "env", "type": "ChoiceParameterDefinition", "description": "target", "defaultValue": "test", "choices": ["test", "prod"] },
                    { "name": "dryRun", "type": "BooleanParameterDefinition", "description": "", "defaultValue": true }
                  ]
                }
              ],
              "builds": [
                { "number": 18, "url": "https://jenkins.example/job/deploy/18/", "result": "SUCCESS", "building": false, "timestamp": 1700000000000, "duration": 1200 }
              ]
            }
            """));
        using var client = CreateClient(handler);

        var job = await client.GetJobAsync("https://jenkins.example/job/deploy/");

        Assert.Equal("deploy", job.Path);
        Assert.Equal(2, job.Parameters.Count);
        Assert.Equal(new[] { "test", "prod" }, job.Parameters[0].Choices);
        Assert.Equal("true", job.Parameters[1].DefaultValue);
        var build = Assert.Single(job.Builds);
        Assert.Equal(18, build.Number);
        Assert.Equal("SUCCESS", build.Result);
        Assert.False(build.Building);
    }

    [Fact]
    public async Task Lists_views_and_jobs_from_a_view()
    {
        using var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/view/team/api/json", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """{ "jobs": [ { "name": "deploy", "url": "https://jenkins.example/view/team/job/deploy/", "_class": "org.jenkinsci.plugins.workflow.job.WorkflowJob", "buildable": true } ] }""");
            }

            return JsonResponse(
                """{ "views": [ { "name": "team", "url": "https://jenkins.example/view/team/", "description": "团队视图" } ] }""");
        });
        using var client = CreateClient(handler);

        var views = await client.ListViewsAsync();
        var view = Assert.Single(views);
        Assert.Equal("team", view.Name);
        Assert.Equal("团队视图", view.Description);

        var jobs = await client.ListJobsInViewAsync(view.Url);

        var job = Assert.Single(jobs);
        Assert.Equal("deploy", job.Name);
        Assert.True(job.Buildable);
    }

    [Fact]
    public async Task Reads_incremental_log_chunk_and_headers()
    {
        using var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("hello\n", Encoding.UTF8, "text/plain")
            };
            response.Headers.Add("X-Text-Size", "6");
            response.Headers.Add("X-More-Data", "true");
            return response;
        });
        using var client = CreateClient(handler);

        var chunk = await client.GetBuildLogChunkAsync("https://jenkins.example/job/deploy/", 4, 0);

        Assert.Equal("hello\n", chunk.Text);
        Assert.Equal(6, chunk.NextStart);
        Assert.True(chunk.MoreData);
        Assert.Contains("/job/deploy/4/logText/progressiveText?start=0", handler.Requests[0].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Lists_queue_items_with_task_and_reason()
    {
        using var handler = new StubHandler((_, _) => JsonResponse(
            """{ "items": [ { "id": 13, "why": "In the quiet period", "blocked": false, "stuck": false, "buildable": true, "inQueueSince": 1700000000000, "task": { "name": "访问baidu", "url": "https://jenkins.example/job/deploy/" } } ] }"""));
        using var client = CreateClient(handler);

        var queue = await client.ListQueueItemsAsync();

        var item = Assert.Single(queue);
        Assert.Equal(13, item.Id);
        Assert.Equal("访问baidu", item.Display);
        Assert.Contains("quiet period", item.StateText, StringComparison.Ordinal);
        Assert.Contains("/queue/api/json", handler.Requests[0].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Reads_artifacts_and_optional_stage_data()
    {
        using var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/3/api/json", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """{ "artifacts": [ { "fileName": "app.zip", "relativePath": "dist/app.zip" } ] }""");
            }

            if (path.EndsWith("/wfapi/describe", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """{ "stages": [ { "name": "Build", "status": "SUCCESS", "durationMillis": 1200 } ] }""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("", Encoding.UTF8, "text/plain")
            };
        });
        using var client = CreateClient(handler);

        var extras = await client.GetBuildExtrasAsync("https://jenkins.example/job/deploy/", 3);

        var artifact = Assert.Single(extras.Artifacts);
        Assert.Equal("app.zip", artifact.FileName);
        Assert.EndsWith("/3/artifact/dist/app.zip", artifact.Url, StringComparison.Ordinal);
        var stage = Assert.Single(extras.Stages);
        Assert.Equal("Build", stage.Name);
        Assert.Equal("成功", stage.StatusText);
        Assert.Null(extras.TestReport);
    }

    [Fact]
    public async Task Stop_accepts_jenkins_redirect_response()
    {
        using var handler = new StubHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/crumbIssuer/api/json"))
                return JsonResponse("""{ "crumb": "abc", "crumbRequestField": "Jenkins-Crumb" }""");

            return new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://jenkins.example/job/deploy/3/") }
            };
        });
        using var client = CreateClient(handler);

        await client.StopBuildAsync("https://jenkins.example/job/deploy/", 3);
    }

    [Fact]
    public async Task Trigger_posts_crumb_and_parameters_and_returns_queue_id()
    {
        using var handler = new StubHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/crumbIssuer/api/json"))
            {
                return JsonResponse("""{ "crumb": "abc123", "crumbRequestField": "Jenkins-Crumb" }""");
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/buildWithParameters"))
            {
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Headers = { Location = new Uri("https://jenkins.example/queue/item/42/") },
                    Content = new StringContent("", Encoding.UTF8, "text/plain")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = CreateClient(handler);

        var result = await client.TriggerBuildAsync(
            "https://jenkins.example/job/deploy/",
            new Dictionary<string, string> { ["env"] = "prod", ["dryRun"] = "false" });

        Assert.Equal(42, result.QueueId);
        var post = handler.Requests.Single(item => item.Method == HttpMethod.Post);
        Assert.Equal("Jenkins-Crumb: abc123", post.Crumb);
        Assert.Contains("env=prod", post.Body, StringComparison.Ordinal);
        Assert.Contains("dryRun=false", post.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_connection_reads_jenkins_version()
    {
        using var handler = new StubHandler((_, _) =>
        {
            var response = JsonResponse("""{ "mode": "NORMAL" }""");
            response.Headers.Add("X-Jenkins", "2.452.2");
            return response;
        });
        using var client = CreateClient(handler);

        var info = await client.TestConnectionAsync();

        Assert.Equal("2.452.2", info.Version);
        Assert.Equal("https://jenkins.example", info.Url);
    }

    [Fact]
    public async Task Unauthorized_response_is_reported_as_authentication_failure()
    {
        using var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("", Encoding.UTF8, "text/plain")
        });
        using var client = CreateClient(handler);

        await Assert.ThrowsAsync<JenkinsAuthenticationException>(() => client.TestConnectionAsync());
    }

    [Fact]
    public async Task Job_url_with_context_path_keeps_relative_path()
    {
        using var handler = new StubHandler((_, _) => JsonResponse(
            """{ "name": "app", "url": "https://jenkins.example/ci/job/team/job/app/", "buildable": true }"""));
        using var client = CreateClient(handler, "https://jenkins.example/ci");

        var job = await client.GetJobAsync("https://jenkins.example/ci/job/team/job/app/");

        Assert.Equal("team/app", job.Path);
    }

    private static JenkinsClient CreateClient(HttpMessageHandler handler, string baseUrl = "https://jenkins.example")
        => new(new JenkinsConnectionConfig
        {
            Id = "test",
            Name = "test",
            BaseUrl = baseUrl,
            Username = "user",
            ApiToken = "token"
        }, handler);

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var crumb = request.Headers.TryGetValues("Jenkins-Crumb", out var values)
                ? $"Jenkins-Crumb: {values.FirstOrDefault()}"
                : null;
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, body, crumb));
            return responder(request, body);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body, string? Crumb);
}
