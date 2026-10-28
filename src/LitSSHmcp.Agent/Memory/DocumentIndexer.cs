using System.Security.Cryptography;
using System.Text;
using LitSSHmcp.Core.Services.Security;

namespace LitSSHmcp.Agent;

/// <summary>
/// 工作区文档增量索引：按文件 hash + embeddings 模型判断是否需要重建；
/// 文档按 <see cref="DocumentChunker"/> 分块后原子替换，避免整文件单向量导致检索粒度粗、旧版本残留。
/// </summary>
public sealed class DocumentIndexer
{
    /// <summary>单文件上限（超过则跳过，避免大文件拖垮启动与 embeddings）。</summary>
    public const int MaxFileBytes = 2_000_000;

    /// <summary>单文件最多保留的分块数（超出只索引前 N 块）。</summary>
    public const int MaxChunksPerFile = 200;

    /// <summary>单次最多枚举的文件数（防止超大目录拖垮启动）。</summary>
    public const int MaxFilesPerRoot = 500;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".json", ".yaml", ".yml", ".ini", ".conf",
        ".sh", ".bash", ".ps1", ".py", ".sql"
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules", "spills", "jobs", "worklogs"
    };

    private readonly AgentMemoryService _memory;

    public DocumentIndexer(AgentMemoryService memory) => _memory = memory;

    /// <summary>索引一个工作区根；返回本次实际重建的文档数。</summary>
    public async Task<int> IndexAsync(string root, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(root))
            return 0;

        string rootFull;
        try
        {
            rootFull = Path.GetFullPath(root);
        }
        catch
        {
            return 0;
        }
        if (!Directory.Exists(rootFull))
            return 0;

        var files = EnumerateFiles(rootFull).Take(MaxFilesPerRoot).ToList();
        var indexed = 0;
        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            async (file, token) =>
            {
                try
                {
                    if (await IndexFileAsync(rootFull, file, token))
                        Interlocked.Increment(ref indexed);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // 单个文件失败不影响其他文档；下次启动会按 hash 重试。
                }
            });
        return indexed;
    }

    private async Task<bool> IndexFileAsync(string rootFull, string file, CancellationToken ct)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(file);
        }
        catch
        {
            return false;
        }
        if (!info.Exists || info.Length <= 0 || info.Length > MaxFileBytes)
            return false;

        var fullPath = Path.GetFullPath(file);
        var docKey = _memory.CreateDocumentKey(fullPath);
        // 文档同样可能含密码/Token/连接串；进入记忆库前统一脱敏（hash 基于脱敏后的内容）。
        var content = SecretRedactor.Redact(await File.ReadAllTextAsync(file, ct));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        if (await _memory.IsDocumentCurrentAsync(docKey, hash, ct))
            return false;

        var chunks = DocumentChunker.Chunk(content);
        if (chunks.Count == 0)
            return false;
        if (chunks.Count > MaxChunksPerFile)
            chunks = chunks.Take(MaxChunksPerFile).ToList();

        var display = Path.GetRelativePath(rootFull, file).Replace('\\', '/');
        var legacySource = "文档:" + display;
        var sources = new string[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
            sources[i] = $"{legacySource}#{i}";

        await _memory.IndexDocumentAsync(docKey, rootFull, legacySource, sources, chunks, hash, ct);
        return true;
    }

    private static IEnumerable<string> EnumerateFiles(string rootFull)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };
        return Directory.EnumerateFiles(rootFull, "*", options)
            .Where(file => IsIndexable(rootFull, file))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsIndexable(string rootFull, string file)
    {
        if (!TextExtensions.Contains(Path.GetExtension(file)))
            return false;

        var relative = Path.GetRelativePath(rootFull, file);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !segments.Any(segment => ExcludedDirectories.Contains(segment));
    }
}
