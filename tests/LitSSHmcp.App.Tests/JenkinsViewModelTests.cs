using System.IO;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Jenkins;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class JenkinsViewModelTests
{
    [Fact]
    public async Task Folder_children_are_loaded_on_demand()
    {
        var node = new JenkinsJobNode(new JenkinsJob
        {
            Name = "team",
            Path = "team",
            Url = "https://jenkins.example/job/team/",
            ClassName = "com.cloudbees.hudson.plugins.folder.Folder",
            IsFolder = true,
            Buildable = false
        }, parent =>
        {
            parent.Children.Add(new JenkinsJobNode(new JenkinsJob
            {
                Name = "app",
                Path = "team/app",
                Url = "https://jenkins.example/job/team/job/app/",
                ClassName = "org.jenkinsci.plugins.workflow.job.WorkflowJob",
                Buildable = true
            }));
            return Task.CompletedTask;
        });

        Assert.True(node.IsFolder);
        Assert.Empty(node.Children);

        await node.EnsureChildrenAsync();

        Assert.True(node.IsLoaded);
        var child = Assert.Single(node.Children);
        Assert.Equal("app", child.Name);
        Assert.True(child.Buildable);
    }

    [Fact]
    public void High_risk_environment_defaults_to_read_only_for_new_connections()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-jenkins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var vm = new JenkinsConnectionEditViewModel(
                new ConfigService(Path.Combine(dir, "config.json"), new DpapiSecretProtector()));

            vm.SelectedEnvironmentIndex = 2;

            Assert.True(vm.ReadOnly);
            Assert.True(vm.RequireConfirmation);

            vm.SelectedEnvironmentIndex = 3;

            Assert.True(vm.ReadOnly);
            Assert.True(vm.RequireConfirmation);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Parameter_fields_use_type_specific_values()
    {
        var boolean = new JenkinsParameterField(new JenkinsParameterDefinition(
            "dryRun", "BooleanParameterDefinition", "", "false", Array.Empty<string>()));
        var choice = new JenkinsParameterField(new JenkinsParameterDefinition(
            "env", "ChoiceParameterDefinition", "", "test", new[] { "test", "prod" }));
        var secret = new JenkinsParameterField(new JenkinsParameterDefinition(
            "token", "PasswordParameterDefinition", "", "", Array.Empty<string>()));

        Assert.True(boolean.IsBoolean);
        Assert.False(boolean.IsText);
        Assert.False(boolean.BoolValue);
        boolean.BoolValue = true;
        Assert.Equal("true", boolean.Value);

        Assert.True(choice.IsChoice);
        Assert.Equal("test", choice.SelectedChoice);
        choice.SelectedChoice = "prod";
        Assert.Equal("prod", choice.Value);

        Assert.True(secret.IsSecret);
        Assert.False(secret.IsText);
    }

    [Fact]
    public void Environment_kinds_are_labels_and_multiple_connections_are_supported()
    {
        var test = new JenkinsConnectionConfig
        {
            Name = "测试",
            EnvironmentKind = JenkinsEnvironmentKinds.Test
        };
        var prod = new JenkinsConnectionConfig
        {
            Name = "生产",
            EnvironmentKind = JenkinsEnvironmentKinds.Production
        };
        var partner = new JenkinsConnectionConfig
        {
            Name = "A 公司 Jenkins",
            EnvironmentKind = JenkinsEnvironmentKinds.Partner,
            EnvironmentLabel = "A 公司第三方"
        };

        Assert.Equal("测试", test.EnvironmentDisplay);
        Assert.Equal("生产", prod.EnvironmentDisplay);
        Assert.Equal("A 公司第三方", partner.EnvironmentDisplay);
        Assert.True(prod.IsHighRisk);
        Assert.True(partner.IsHighRisk);
        Assert.False(test.IsHighRisk);
        Assert.NotEqual(test.Id, prod.Id);
        Assert.NotEqual(prod.Id, partner.Id);
    }
}
