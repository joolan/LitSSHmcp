using System.IO;
using System.Windows;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Search;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class RemoteScriptEditorWindow : FluentWindow
{
    private readonly ScriptEditContext _context;
    private bool _saving;

    public RemoteScriptEditorWindow(ScriptEditContext context)
    {
        InitializeComponent();
        _context = context;
        Title = "编辑脚本 - " + Path.GetFileName(context.FilePath);
        Bar.Title = Title;
        PathText.Text = context.FilePath;
        MetaText.Text = context.Meta;
        InterpreterText.Text = "解释器：" + (string.IsNullOrWhiteSpace(context.Interpreter) ? "未知" : context.Interpreter);
        EncodingText.Text = "编码：" + context.Encoding.WebName;
        Editor.Text = context.Content;
        Editor.TextChanged += (_, _) => UpdateLineCount();
        ApplySyntaxHighlighting(context.FilePath);
        SearchPanel.Install(Editor.TextArea);
        UpdateLineCount();
        WindowLayout.Attach(this, "scheduled-task-script-editor");
    }

    public bool Saved { get; private set; }

    private void UpdateLineCount()
        => LineCountText.Text = $"共 {Editor.Document.LineCount} 行";

    private void ApplySyntaxHighlighting(string path)
    {
        try
        {
            var definition = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(path));
            if (definition is not null)
            {
                Editor.SyntaxHighlighting = definition;
            }
        }
        catch
        {
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
        => AsyncOperation.Run("保存远程脚本", SaveEditorAsync);

    private async Task SaveEditorAsync()
    {
        if (_saving)
        {
            return;
        }

        _saving = true;
        StatusText.Text = "正在保存…";
        try
        {
            var result = await _context.SaveAsync(Editor.Text);
            StatusText.Text = result.Message;
            if (result.Success)
            {
                Saved = true;
                DialogResult = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存失败：" + ex.Message;
        }
        finally
        {
            _saving = false;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
