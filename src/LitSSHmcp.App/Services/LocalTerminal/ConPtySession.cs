using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LitSSHmcp.App.Services.LocalTerminal;

/// <summary>本机命令执行结果（干净输出 + 退出码 + 工作目录）。</summary>
public sealed record CommandResult(string Output, int ExitCode, bool TimedOut, bool Busy = false, string WorkingDirectory = "", bool Killed = false);

/// <summary>
/// Windows 伪控制台（ConPTY）会话：把本机 shell（默认 PowerShell）当作真实终端运行，
/// 输出以 UTF-8 文本事件抛出，输入/尺寸变化按 PTY 语义回写。用于「本机终端」标签，
/// 以及 AI 通过共享会话执行 CLI。
/// </summary>
public sealed class ConPtySession : IDisposable
{
    private IntPtr _hPC = IntPtr.Zero;
    private IntPtr _inputWrite = IntPtr.Zero;
    private IntPtr _outputRead = IntPtr.Zero;
    private IntPtr _hProcess = IntPtr.Zero;
    private IntPtr _hThread = IntPtr.Zero;
    private IntPtr _hJob = IntPtr.Zero;
    private FileStream? _input;
    private FileStream? _output;
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;
    private bool _disposed;

    /// <summary>收到终端输出（已按 UTF-8 增量解码）。</summary>
    public event Action<string>? Output;

    /// <summary>进程退出时触发（可能在其他线程）。</summary>
    public event Action? Exited;

    public bool IsRunning { get; private set; }
    public int ProcessId { get; private set; }

    /// <summary>启动 shell。默认优先 pwsh，找不到则 powershell.exe。</summary>
    public void Start(short cols, short rows, string? shellPath = null, string? workingDirectory = null, string? arguments = null)
    {
        if (IsRunning)
            return;
        if (cols <= 0) cols = 80;
        if (rows <= 0) rows = 24;

        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = false
        };

        if (!CreatePipe(out var inRead, out var inWrite, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(input) 失败");
        if (!CreatePipe(out var outRead, out var outWrite, ref sa, 0))
        {
            CloseHandle(inRead);
            CloseHandle(inWrite);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(output) 失败");
        }

        var size = new COORD(cols, rows);
        var hr = CreatePseudoConsole(size, inRead, outWrite, 0, out _hPC);
        if (hr != 0)
        {
            CloseHandle(inRead);
            CloseHandle(inWrite);
            CloseHandle(outRead);
            CloseHandle(outWrite);
            throw new Win32Exception(hr, "CreatePseudoConsole 失败");
        }

        // 输入读端 / 输出写端由控制台持有，须等子进程挂上控制台后再关闭
        _inputWrite = inWrite;
        _outputRead = outRead;

        var cmd = ResolveShell(shellPath);
        var cwd = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : workingDirectory;

        var startup = new STARTUPINFOEX();
        startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        startup.StartupInfo.dwFlags = 0x00000100; // STARTF_USESTDHANDLES，std 句柄留空(0)，避免继承父进程控制台
        IntPtr attrList = IntPtr.Zero;
        try
        {
            var bytes = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            attrList = Marshal.AllocHGlobal(bytes);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref bytes))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList 失败");
            if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute 失败");
            startup.lpAttributeList = attrList;

            const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;

            var cmdLine = new StringBuilder("\"" + cmd + "\" " + (arguments ?? "-NoLogo -NoProfile"));
            if (!CreateProcess(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero, cwd, ref startup, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess 失败");

            _hProcess = pi.hProcess;
            _hThread = pi.hThread;
            ProcessId = pi.dwProcessId;

            // 放入 Job Object（KILL_ON_JOB_CLOSE）：关闭/强杀时整棵子进程树一起结束。
            try
            {
                _hJob = CreateJobObjectW(IntPtr.Zero, null);
                if (_hJob != IntPtr.Zero)
                {
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                    {
                        BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                        {
                            LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                        }
                    };
                    var jobSize = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                    var ptr = Marshal.AllocHGlobal(jobSize);
                    try
                    {
                        Marshal.StructureToPtr(info, ptr, false);
                        SetInformationJobObject(_hJob, JobObjectExtendedLimitInformation, ptr, (uint)jobSize);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                    AssignProcessToJobObject(_hJob, _hProcess);
                }
            }
            catch
            {
                // 已在别的 Job 中/权限不足：退化为仅杀顶层进程
                if (_hJob != IntPtr.Zero)
                {
                    try { CloseHandle(_hJob); } catch { /* ignore */ }
                    _hJob = IntPtr.Zero;
                }
            }

            // 子进程已挂上控制台，控制台自己持有的句柄可关闭
            CloseHandle(inRead);
            CloseHandle(outWrite);
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
        }

        _input = new FileStream(new SafeFileHandle(_inputWrite, ownsHandle: false), FileAccess.Write, 4096);
        _output = new FileStream(new SafeFileHandle(_outputRead, ownsHandle: false), FileAccess.Read, 4096);
        IsRunning = true;
        _readLoop = Task.Run(ReadLoop);
    }

    private static string ResolveShell(string? shellPath)
    {
        if (!string.IsNullOrWhiteSpace(shellPath) && File.Exists(shellPath))
            return shellPath!;
        foreach (var candidate in new[] { "pwsh.exe", "powershell.exe" })
        {
            var resolved = ResolveOnPath(candidate);
            if (resolved is not null)
                return resolved;
        }
        return Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static string? ResolveOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full))
                    return full;
            }
            catch
            {
                // 忽略非法路径项
            }
        }
        return null;
    }

    private void ReadLoop()
    {
        var utf8 = Encoding.UTF8.GetDecoder();
        var buffer = new byte[8192];
        var chars = new char[8192];
        try
        {
            while (!_cts.IsCancellationRequested && _output is not null)
            {
                int n;
                try { n = _output.Read(buffer, 0, buffer.Length); }
                catch { break; }
                if (n <= 0)
                    break;

                utf8.Convert(buffer, 0, n, chars, 0, chars.Length, false,
                    out _, out var used, out _);
                if (used > 0)
                    Output?.Invoke(new string(chars, 0, used));
            }
        }
        catch
        {
            // 会话结束
        }
        finally
        {
            IsRunning = false;
            try { Exited?.Invoke(); } catch { /* ignore */ }
        }
    }

    /// <summary>把文本写入 PTY（回车需自行带上 \r）。</summary>
    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _input?.Write(bytes, 0, bytes.Length);
            _input?.Flush();
        }
        catch
        {
            // 会话已退出
        }
    }

    public void WriteBytes(byte[] data)
    {
        if (data is { Length: > 0 })
        {
            try { _input?.Write(data, 0, data.Length); _input?.Flush(); }
            catch { /* ignore */ }
        }
    }

    public void Resize(short cols, short rows)
    {
        if (_hPC == IntPtr.Zero || cols <= 0 || rows <= 0)
            return;
        try { ResizePseudoConsole(_hPC, new COORD(cols, rows)); }
        catch { /* ignore */ }
    }

    private readonly SemaphoreSlim _runGate = new(1, 1);
    private string _tempDir = string.Empty;

    /// <summary>
    /// 在共享会话里执行一条命令并返回**干净输出**、退出码与当前工作目录：
    /// 把命令写入临时 .ps1（点源执行以保留 cwd/env），用 Tee 一边实时显示在终端、一边写入临时文件，
    /// 再用唯一标记（含退出码与 $PWD）判断完成。超时先 Ctrl+C 等其回到提示符，宽限期仍未回则强杀整棵进程树。
    /// </summary>
    public async Task<CommandResult> RunCommandAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsRunning)
            throw new InvalidOperationException("本地 shell 未启动");

        // 共享终端一次只跑一条命令：若上一条尚未结束，直接拒绝（不排队），避免命令交错。
        if (!_runGate.Wait(0))
            return new CommandResult(string.Empty, -1, false, Busy: true);
        try
        {
            _tempDir = string.IsNullOrEmpty(_tempDir)
                ? Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "litssh-ai")).FullName
                : _tempDir;
            var id = Guid.NewGuid().ToString("N");
            var scriptPath = Path.Combine(_tempDir, id + ".ps1");
            var outPath = Path.Combine(_tempDir, id + ".out");
            await File.WriteAllTextAsync(scriptPath, command ?? string.Empty, new UTF8Encoding(true), ct).ConfigureAwait(false);

            var token = "LITSSH_" + id;
            var marker = "<<<" + token + ">>>";

            // 只保留最近的输出用于标记检测（避免大输出把内存撑爆）
            var sink = new StringBuilder();
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnOutput(string text)
            {
                lock (sink)
                {
                    sink.Append(text);
                    if (sink.Length > 262144)
                        sink.Remove(0, sink.Length - 131072);
                    if (sink.ToString().Contains(marker, StringComparison.Ordinal))
                        tcs.TrySetResult(true);
                }
            }

            Output += OnOutput;
            try
            {
                // 标记用拼接避免被命令行回显提前命中；命令输出经 Tee 实时显示并落盘。
                var scriptArg = scriptPath.Replace("'", "''");
                var outArg = outPath.Replace("'", "''");
                Write(
                    $"$__ec=0; . '{scriptArg}' *>&1 | Tee-Object -FilePath '{outArg}' | Out-Host; " +
                    $"$__ec=$LASTEXITCODE; $__m='<<<'+'{token}'+'>>>'; " +
                    "Write-Host ($__m + '|' + $__ec + '|' + $PWD.Path)\r");

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
                var timedOut = completed != tcs.Task;
                var killed = false;
                if (timedOut)
                {
                    // 先尝试 Ctrl+C 回提示符；宽限期内未回到则强杀整棵进程树（终端会变为已退出，下次自动重开）。
                    try { Write("\u0003"); } catch { /* ignore */ }
                    if (!await WaitForReadyAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
                    {
                        killed = true;
                        try { TerminateJobObject(); } catch { /* ignore */ }
                    }
                }

                var output = string.Empty;
                try
                {
                    if (File.Exists(outPath))
                        output = StripAnsi(await ReadCapturedTextAsync(outPath, ct).ConfigureAwait(false));
                }
                catch { /* 读取失败忽略 */ }

                ParseResult(sink.ToString(), marker, out var exitCode, out var cwd);
                return new CommandResult(output, exitCode, timedOut, WorkingDirectory: cwd, Killed: killed);
            }
            finally
            {
                Output -= OnOutput;
                try { File.Delete(scriptPath); } catch { /* ignore */ }
                try { File.Delete(outPath); } catch { /* ignore */ }
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>等待 shell 回到可接受输入（提示符）状态：发一个“就绪”标记并等它被执行输出。</summary>
    private async Task<bool> WaitForReadyAsync(TimeSpan grace, CancellationToken ct)
    {
        var token = "LITSSH_READY_" + Guid.NewGuid().ToString("N");
        var ready = "<<<" + token + ">>>";
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOutput(string text)
        {
            if (text.Contains(ready, StringComparison.Ordinal))
                tcs.TrySetResult(true);
        }

        Output += OnOutput;
        try
        {
            // 同样用拼接避免回显误判
            Write($"$__r='<<<'+'{token}'+'>>>'; Write-Host $__r\r");
            var done = await Task.WhenAny(tcs.Task, Task.Delay(grace, ct)).ConfigureAwait(false);
            return done == tcs.Task;
        }
        catch
        {
            return false;
        }
        finally
        {
            Output -= OnOutput;
        }
    }

    private static async Task<string> ReadCapturedTextAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string StripAnsi(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        // CSI 序列 + OSC（标题）等控制序列
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, "\u001B\\[[0-?]*[ -/]*[@-~]", string.Empty);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "\u001B\\][^\u0007]*(?:\u0007|\u001B\\\\)", string.Empty);
        return cleaned;
    }

    private static void ParseResult(string text, string marker, out int exitCode, out string cwd)
    {
        exitCode = 0;
        cwd = string.Empty;
        var idx = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            exitCode = -1;
            return;
        }

        var rest = text[(idx + marker.Length)..];
        var nl = rest.IndexOfAny(new[] { '\r', '\n' });
        if (nl >= 0)
            rest = rest[..nl];
        // rest 形如 "|<ec>|<cwd>"
        var p1 = rest.IndexOf('|');
        var p2 = p1 >= 0 ? rest.IndexOf('|', p1 + 1) : -1;
        if (p1 >= 0 && p2 >= 0)
        {
            var ec = rest[(p1 + 1)..p2].Trim();
            exitCode = int.TryParse(ec, out var v) ? v : 0;
            cwd = rest[(p2 + 1)..].Trim();
        }
    }

    /// <summary>强杀整棵进程树（Job Object）。</summary>
    public void TerminateJobObject()
    {
        if (_hJob != IntPtr.Zero)
        {
            try { TerminateJobObjectNative(_hJob, 0); } catch { /* ignore */ }
        }
        else if (_hProcess != IntPtr.Zero)
        {
            try { TerminateProcess(_hProcess, 0); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        IsRunning = false;
        try { _cts.Cancel(); } catch { /* ignore */ }

        try { _input?.Dispose(); } catch { /* ignore */ }
        try { _output?.Dispose(); } catch { /* ignore */ }

        if (_hPC != IntPtr.Zero)
        {
            try { ClosePseudoConsole(_hPC); } catch { /* ignore */ }
            _hPC = IntPtr.Zero;
        }
        if (_hJob != IntPtr.Zero)
        {
            try { TerminateJobObjectNative(_hJob, 0); } catch { /* ignore */ }
            try { CloseHandle(_hJob); } catch { /* ignore */ }   // KILL_ON_JOB_CLOSE 兜底
            _hJob = IntPtr.Zero;
        }
        if (_hProcess != IntPtr.Zero)
        {
            try { TerminateProcess(_hProcess, 0); } catch { /* ignore */ }
            try { CloseHandle(_hProcess); } catch { /* ignore */ }
            _hProcess = IntPtr.Zero;
        }
        if (_hThread != IntPtr.Zero)
        {
            try { CloseHandle(_hThread); } catch { /* ignore */ }
            _hThread = IntPtr.Zero;
        }
        _inputWrite = IntPtr.Zero;
        _outputRead = IntPtr.Zero;
        _cts.Dispose();
    }

    // ---- P/Invoke ----

    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
        public COORD(short x, short y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint flags, out IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, uint attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // ---- Job Object（进程树管理）----

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "TerminateJobObject")]
    private static extern bool TerminateJobObjectNative(IntPtr hJob, uint uExitCode);
}
