using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LitSSHmcp.App.Services.LocalTerminal;

/// <summary>本机命令执行结果（干净输出 + 退出码）。</summary>
public sealed record CommandResult(string Output, int ExitCode, bool TimedOut);

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
    /// 在共享会话里执行一条命令并返回**干净输出**与退出码：
    /// 把命令写入临时 .ps1（点源执行以保留 cwd/env），全部流重定向到临时文件，
    /// 再用唯一标记判断执行完成。命令会真实显示在终端里（用户可见）。
    /// </summary>
    public async Task<CommandResult> RunCommandAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsRunning)
            throw new InvalidOperationException("本地 shell 未启动");

        await _runGate.WaitAsync(ct).ConfigureAwait(false);
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
            var marker = "<<<" + token + "_DONE>>>";

            var sink = new StringBuilder();
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnOutput(string text)
            {
                lock (sink)
                {
                    sink.Append(text);
                    if (sink.ToString().Contains(marker, StringComparison.Ordinal))
                        tcs.TrySetResult(true);
                }
            }

            Output += OnOutput;
            try
            {
                Write($"$__ec=0; . '{scriptPath}' *> '{outPath}'; $__ec=$LASTEXITCODE; " +
                      $"Get-Content -LiteralPath '{outPath}' -Raw | Out-Host; Write-Host \"{marker} $__ec\"\r");

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
                var timedOut = completed != tcs.Task;
                if (timedOut)
                {
                    try { Write("\u0003"); } catch { /* 发 Ctrl+C 中断 */ }
                }

                var output = string.Empty;
                try
                {
                    if (File.Exists(outPath))
                        output = await File.ReadAllTextAsync(outPath, ct).ConfigureAwait(false);
                }
                catch { /* 读取失败忽略 */ }

                var exitCode = ParseExitCode(sink.ToString(), marker);
                return new CommandResult(output, exitCode, timedOut);
            }
            finally
            {
                Output -= OnOutput;
                try { File.Delete(scriptPath); } catch { /* ignore */ }
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    private static int ParseExitCode(string text, string marker)
    {
        var idx = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return -1;
        var rest = text[(idx + marker.Length)..].TrimStart();
        var end = 0;
        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '-'))
            end++;
        return int.TryParse(rest[..end], out var code) ? code : 0;
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
}
