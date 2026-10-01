using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// 敏感操作授权确认对话框：强制置顶、无操作超时自动拒绝、自绘外观。
/// </summary>
internal sealed class ApprovalDialog : Form
{
    private static readonly Color HeaderBack = Color.FromArgb(31, 42, 68);
    private static readonly Color AllowBack = Color.FromArgb(30, 142, 62);
    private static readonly Color TextPrimary = Color.FromArgb(40, 40, 40);
    private static readonly Color TextMuted = Color.FromArgb(120, 120, 120);

    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Label _countdown = new();
    private readonly string _command;
    private readonly bool _topMost;
    private int _remaining;

    /// <summary>是否为超时自动拒绝。</summary>
    public bool TimedOut { get; private set; }

    private const int SW_SHOW = 5;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public ApprovalDialog(
        string serverName,
        string operation,
        string command,
        string? filePath,
        int timeoutSeconds,
        bool topMost)
    {
        _command = command;
        _topMost = topMost;

        Text = "LitSSH MCP - 操作确认";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = false;
        TopMost = topMost;
        ClientSize = new Size(640, 500);
        MinimumSize = new Size(640, 500);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.White;

        Controls.Add(BuildBody(serverName, operation, filePath));
        Controls.Add(BuildFooter(timeoutSeconds));
        Controls.Add(BuildHeader());

        Shown += (_, _) =>
        {
            Activate();
            BringToFront();
        };

        FormClosed += (_, _) =>
        {
            _timer.Stop();
            _timer.Dispose();
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        ForceForeground();

        // 再抢一次前台/置顶，避免被宿主窗口盖住
        var boost = new System.Windows.Forms.Timer { Interval = 300 };
        boost.Tick += (_, _) =>
        {
            boost.Stop();
            boost.Dispose();
            ForceForeground();
        };
        boost.Start();
    }

    private void ForceForeground()
    {
        try
        {
            TopMost = _topMost;
            WindowState = FormWindowState.Normal;
            ShowWindow(Handle, SW_SHOW);
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            BringToFront();
            Activate();
            SetForegroundWindow(Handle);
        }
        catch
        {
            // 忽略：无权限时不影响弹窗本身
        }
    }

    private Panel BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = HeaderBack,
            Padding = new Padding(18, 10, 18, 10)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowCount = 2;

        var title = new Label
        {
            Text = "操作确认",
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0)
        };
        var subtitle = new Label
        {
            Text = "即将执行敏感操作，请确认后放行",
            ForeColor = Color.FromArgb(198, 208, 226),
            Font = new Font("Microsoft YaHei UI", 8.5F),
            AutoSize = true,
            Margin = new Padding(1, 8, 0, 0)
        };

        header.Controls.Add(title, 0, 0);
        header.Controls.Add(subtitle, 0, 1);
        return header;
    }

    private Panel BuildBody(string serverName, string operation, string? filePath)
    {
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 8) };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = Color.White
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(table, "服务器:", serverName);
        AddRow(table, "操作类型:", operation);
        if (!string.IsNullOrEmpty(filePath))
            AddRow(table, "文件/目标:", filePath!);

        table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        var cmdLabel = new Label
        {
            Text = "内容:",
            ForeColor = TextPrimary,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Margin = new Padding(0, 4, 0, 0),
            AutoSize = true
        };
        table.Controls.Add(cmdLabel, 0, table.RowCount - 1);

        var cmdBox = new TextBox
        {
            Text = _command,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F),
            BackColor = Color.FromArgb(248, 249, 251),
            BorderStyle = BorderStyle.FixedSingle
        };
        table.Controls.Add(cmdBox, 1, table.RowCount - 1);
        table.RowStyles[table.RowCount - 1] = new RowStyle(SizeType.Percent, 100);

        body.Controls.Add(table);
        return body;
    }

    private void AddRow(TableLayoutPanel table, string label, string value)
    {
        table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var key = new Label
        {
            Text = label,
            ForeColor = TextPrimary,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 6),
            AutoSize = true
        };
        var val = new Label
        {
            Text = value,
            ForeColor = TextPrimary,
            Margin = new Padding(0, 0, 0, 6),
            AutoSize = true,
            MaximumSize = new Size(470, 0)
        };

        table.Controls.Add(key, 0, table.RowCount - 1);
        table.Controls.Add(val, 1, table.RowCount - 1);
    }

    private Panel BuildFooter(int timeoutSeconds)
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(18, 12, 18, 12) };

        _countdown.TextAlign = ContentAlignment.MiddleLeft;
        _countdown.Dock = DockStyle.Left;
        _countdown.Width = 240;
        _countdown.ForeColor = TextMuted;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true
        };

        var allow = new Button
        {
            Text = "允许执行",
            Width = 100,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = AllowBack,
            ForeColor = Color.White,
            DialogResult = DialogResult.OK
        };
        allow.FlatAppearance.BorderSize = 0;

        var reject = new Button
        {
            Text = "拒绝",
            Width = 80,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(238, 238, 238),
            ForeColor = TextPrimary,
            DialogResult = DialogResult.Cancel
        };
        reject.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);

        var copy = new Button
        {
            Text = "复制内容",
            Width = 104,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(238, 238, 238),
            ForeColor = TextPrimary
        };
        copy.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_command); } catch { /* ignore */ }
        };

        buttons.Controls.Add(allow);
        buttons.Controls.Add(reject);
        buttons.Controls.Add(copy);

        footer.Controls.Add(_countdown);
        footer.Controls.Add(buttons);

        AcceptButton = reject;
        CancelButton = reject;

        _remaining = timeoutSeconds;
        if (timeoutSeconds > 0)
        {
            _countdown.Text = $"距自动拒绝还有 {_remaining} 秒";
            _timer.Interval = 1000;
            _timer.Tick += (_, _) =>
            {
                _remaining--;
                if (_remaining <= 0)
                {
                    _timer.Stop();
                    TimedOut = true;
                    DialogResult = DialogResult.Cancel;
                    Close();
                }
                else
                {
                    _countdown.Text = $"距自动拒绝还有 {_remaining} 秒";
                }
            };
            _timer.Start();
        }
        else
        {
            _countdown.Text = "无自动拒绝超时";
        }

        return footer;
    }
}
