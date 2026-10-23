using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// 敏感操作授权确认对话框：强制置顶、无操作超时自动拒绝、自绘外观。
/// 支持三档放行范围（仅本次 / 本轮次内 / 整个会话），默认仅本次。
/// </summary>
internal sealed class ApprovalDialog : Form
{
    private static readonly Color HeaderBack = Color.FromArgb(31, 42, 68);
    private static readonly Color AllowBack = Color.FromArgb(30, 142, 62);
    private static readonly Color DangerText = Color.FromArgb(178, 52, 52);
    private static readonly Color TextPrimary = Color.FromArgb(40, 40, 40);
    private static readonly Color TextMuted = Color.FromArgb(120, 120, 120);
    private static readonly Color CmdBack = Color.FromArgb(248, 249, 251);
    private static readonly Color Border = Color.FromArgb(210, 215, 222);
    private static readonly Color BadgeBack = Color.FromArgb(255, 243, 224);
    private static readonly Color BadgeText = Color.FromArgb(150, 88, 10);

    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Label _countdown = new();
    private readonly CheckBox _chkRound = new();
    private readonly CheckBox _chkSession = new();
    private readonly string _command;
    private readonly bool _topMost;
    private int _remaining;

    /// <summary>是否为超时自动拒绝。</summary>
    public bool TimedOut { get; private set; }

    /// <summary>用户选择的放行范围（弹窗关闭后读取）。</summary>
    public ApprovalScope Scope =>
        _chkSession.Checked ? ApprovalScope.Session :
        _chkRound.Checked ? ApprovalScope.Round :
        ApprovalScope.Once;

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
        bool topMost,
        IReadOnlyList<string>? sessionGrantedTools = null,
        string? riskReport = null,
        string? sessionLabel = null)
    {
        _command = command;
        _topMost = topMost;

        Text = "LitSSH MCP - 敏感操作审批";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = false;
        TopMost = topMost;
        // 有风险报告时加宽为左右分栏（左命令右风险），无报告时保持原尺寸
        var hasRisk = !string.IsNullOrWhiteSpace(riskReport);
        ClientSize = new Size(hasRisk ? 860 : 680, 560);
        MinimumSize = new Size(hasRisk ? 860 : 680, 560);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.White;

        Controls.Add(BuildBody(serverName, operation, filePath, sessionGrantedTools, riskReport));
        Controls.Add(BuildFooter(timeoutSeconds));
        Controls.Add(BuildHeader(serverName, sessionLabel));

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

    private Panel BuildHeader(string serverName, string? sessionLabel = null)
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = HeaderBack,
            Padding = new Padding(18, 12, 18, 12)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowCount = 2;

        var title = new Label
        {
            Text = "敏感操作审批",
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0)
        };
        // 并行会话后桌面会同时出现多个弹窗：必须标明所属会话，避免审批错认（评估报告 §4.2）。
        var subtitleText = string.IsNullOrWhiteSpace(sessionLabel)
            ? $"服务器：{serverName}"
            : $"服务器：{serverName} · 会话：{sessionLabel}";
        var subtitle = new Label
        {
            Text = subtitleText,
            ForeColor = Color.FromArgb(198, 208, 226),
            Font = new Font("Microsoft YaHei UI", 8.5F),
            AutoSize = true,
            Margin = new Padding(1, 8, 0, 0)
        };

        header.Controls.Add(title, 0, 0);
        header.Controls.Add(subtitle, 0, 1);
        return header;
    }

    private Panel BuildBody(string serverName, string operation, string? filePath, IReadOnlyList<string>? sessionGrantedTools, string? riskReport)
    {
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 8) };

        // 命令框
        var cmdBox = new TextBox
        {
            Text = _command,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5F),
            BackColor = CmdBack,
            BorderStyle = BorderStyle.FixedSingle
        };

        // 内容区：无风险报告=单栏命令；有报告=左右分栏（左命令右风险）
        Control contentArea;
        if (string.IsNullOrWhiteSpace(riskReport))
        {
            contentArea = cmdBox;
        }
        else
        {
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.Panel2,
                SplitterDistance = 430,
                Panel1MinSize = 260,
                Panel2MinSize = 260,
                BackColor = Border
            };

            var riskHeader = BuildSectionHeader("脚本风险分析（服务端规则扫描）");
            var riskBox = new TextBox
            {
                Text = riskReport,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9F),
                BackColor = CmdBack,
                BorderStyle = BorderStyle.FixedSingle
            };
            var riskPanel = new Panel { Dock = DockStyle.Fill };
            riskPanel.Controls.Add(riskBox);
            riskPanel.Controls.Add(riskHeader);   // Fill 先入列，Top 后布局

            split.Panel1.Controls.Add(cmdBox);
            split.Panel2.Controls.Add(riskPanel);
            contentArea = split;
        }

        // 放行范围区（底部）
        var scope = BuildScopePanel(sessionGrantedTools);

        // 命令区标题行：标签 + 复制按钮
        var cmdHeaderRow = BuildSectionHeader("即将执行的内容");
        var copy = new Button
        {
            Text = "复制",
            Width = 56,
            Height = 22,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(238, 238, 238),
            ForeColor = TextPrimary,
            Margin = new Padding(0, 2, 0, 0)
        };
        copy.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_command); } catch { /* ignore */ }
        };
        cmdHeaderRow.Controls.Add(copy, 1, 0);

        // 元信息行：操作类型徽章 + 文件/目标
        var meta = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 2)
        };
        meta.Controls.Add(new Label
        {
            Text = operation,
            AutoSize = true,
            BackColor = BadgeBack,
            ForeColor = BadgeText,
            Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold),
            Padding = new Padding(8, 3, 8, 3),
            Margin = new Padding(0, 0, 8, 0)
        });
        if (!string.IsNullOrEmpty(filePath))
        {
            meta.Controls.Add(new Label
            {
                Text = $"目标：{filePath}",
                AutoSize = true,
                ForeColor = TextMuted,
                MaximumSize = new Size(480, 0),
                Margin = new Padding(0, 4, 0, 0)
            });
        }

        // 加入顺序 = 反向 Dock：Fill 最先入列最后布局
        body.Controls.Add(contentArea);
        body.Controls.Add(scope);
        body.Controls.Add(cmdHeaderRow);
        body.Controls.Add(meta);
        return body;
    }

    /// <summary>内容区标题行（左标题 label + 右侧按钮位预留）。</summary>
    private static TableLayoutPanel BuildSectionHeader(string title)
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Height = 26,
            Margin = new Padding(0, 6, 0, 0)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label
        {
            Text = title,
            ForeColor = TextPrimary,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        }, 0, 0);
        return header;
    }

    private Panel BuildScopePanel(IReadOnlyList<string>? sessionGrantedTools)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(0, 10, 0, 0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var caption = new Label
        {
            Text = "放行范围（默认仅允许一次）",
            ForeColor = TextPrimary,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };

        _chkRound.Text = "本轮次内自动放行同一目标与相同操作（发送下一条用户消息后恢复审批）";
        _chkRound.AutoSize = true;
        _chkRound.ForeColor = TextPrimary;
        _chkRound.Margin = new Padding(4, 2, 0, 2);

        _chkSession.Text = "整个助手运行期间自动放行同一目标与相同操作（不建议）";
        _chkSession.AutoSize = true;
        _chkSession.ForeColor = DangerText;
        _chkSession.Margin = new Padding(4, 2, 0, 2);

        // 二选一：勾一个自动取消另一个
        _chkRound.CheckedChanged += (_, _) =>
        {
            if (_chkRound.Checked)
                _chkSession.Checked = false;
        };
        _chkSession.CheckedChanged += (_, _) =>
        {
            if (_chkSession.Checked)
                _chkRound.Checked = false;
        };

        panel.Controls.Add(caption, 0, 0);
        panel.RowCount = 1;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(_chkRound, 0, panel.RowCount);
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowCount++;
        panel.Controls.Add(_chkSession, 0, panel.RowCount);
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowCount++;

        if (sessionGrantedTools is { Count: > 0 })
        {
            var hint = new Label
            {
                Text = "已有会话级授权（工具 @ 目标）：" + string.Join("、", sessionGrantedTools),
                ForeColor = TextMuted,
                Font = new Font("Microsoft YaHei UI", 8F),
                AutoSize = true,
                MaximumSize = new Size(620, 0),
                Margin = new Padding(4, 6, 0, 0)
            };
            panel.Controls.Add(hint, 0, panel.RowCount);
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowCount++;
        }

        return panel;
    }

    private Panel BuildFooter(int timeoutSeconds)
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(18, 12, 18, 12) };

        _countdown.TextAlign = ContentAlignment.MiddleLeft;
        _countdown.Dock = DockStyle.Left;
        _countdown.Width = 260;
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

        buttons.Controls.Add(allow);
        buttons.Controls.Add(reject);

        footer.Controls.Add(_countdown);
        footer.Controls.Add(buttons);

        // 保险丝：Enter = 拒绝（防误触回车直接放行）
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
