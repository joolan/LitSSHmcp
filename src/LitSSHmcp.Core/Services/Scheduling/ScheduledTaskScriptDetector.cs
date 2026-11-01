namespace LitSSHmcp.Core.Services.Scheduling;

public static class ScheduledTaskScriptDetector
{
    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh", "bash", "dash", "zsh", "ksh", "ash", "fish",
        "python", "python2", "python3",
        "perl", "ruby", "php", "node", "nodejs", "pwsh", "powershell",
        "lua", "Rscript", "java"
    };

    private static readonly HashSet<string> PrefixCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "nohup", "setsid", "stdbuf", "command", "exec", "time"
    };

    public static bool TryDetect(string? command, out string scriptPath, out string interpreter)
    {
        scriptPath = string.Empty;
        interpreter = string.Empty;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var tokens = Tokenize(command);
        var currentDirectory = string.Empty;
        var commandSeen = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Length == 0)
            {
                continue;
            }

            if (IsSeparator(token))
            {
                commandSeen = false;
                continue;
            }

            if (IsRedirection(token))
            {
                if (token is ">" or ">>" or "<" or "2>" or "&>")
                {
                    i++;
                }

                continue;
            }

            if (LooksLikeAssignment(token))
            {
                continue;
            }

            if (token.Equals("cd", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < tokens.Count)
                {
                    currentDirectory = ResolvePath(currentDirectory, tokens[++i]);
                }

                continue;
            }

            if (token.Equals("env", StringComparison.OrdinalIgnoreCase))
            {
                while (i + 1 < tokens.Count &&
                       (tokens[i + 1].StartsWith('-') || LooksLikeAssignment(tokens[i + 1])))
                {
                    i++;
                }

                continue;
            }

            if (token.Equals("sudo", StringComparison.OrdinalIgnoreCase))
            {
                i = SkipSudoOptions(tokens, i);
                continue;
            }

            if (token.Equals("nice", StringComparison.OrdinalIgnoreCase))
            {
                i = SkipOptionWithArgument(tokens, i, "-n");
                continue;
            }

            if (token.Equals("timeout", StringComparison.OrdinalIgnoreCase))
            {
                i = SkipTimeoutOptions(tokens, i);
                continue;
            }

            if (token.Equals("flock", StringComparison.OrdinalIgnoreCase))
            {
                i = SkipFlockOptions(tokens, i);
                continue;
            }

            if (PrefixCommands.Contains(token))
            {
                continue;
            }

            if (token.StartsWith('-'))
            {
                continue;
            }

            var name = FileName(token);
            if (Interpreters.Contains(name))
            {
                i++;
                while (i < tokens.Count && tokens[i].StartsWith('-'))
                {
                    var option = tokens[i];
                    if (option is "-c" or "--command" or "-m" or "--module" or "-e" or "--eval")
                    {
                        return false;
                    }

                    i++;
                }

                if (i < tokens.Count && TryGetScriptPath(tokens[i], currentDirectory, out scriptPath))
                {
                    interpreter = name;
                    return true;
                }

                return false;
            }

            if (!commandSeen)
            {
                commandSeen = true;
                if (TryGetScriptPath(token, currentDirectory, out scriptPath))
                {
                    interpreter = string.Empty;
                    return true;
                }
            }
        }

        return false;
    }

    private static int SkipSudoOptions(List<string> tokens, int index)
    {
        var optionsWithArguments = new HashSet<string>(StringComparer.Ordinal)
        {
            "-u", "-g", "-p", "-C", "-h", "-r", "-t", "-U", "-T", "-R", "-D"
        };

        while (index + 1 < tokens.Count && tokens[index + 1].StartsWith('-'))
        {
            var option = tokens[index + 1];
            index++;
            if (option == "--")
            {
                break;
            }

            if (optionsWithArguments.Contains(option) && index + 1 < tokens.Count)
            {
                index++;
            }
        }

        return index;
    }

    private static int SkipOptionWithArgument(List<string> tokens, int index, string option)
    {
        while (index + 1 < tokens.Count && tokens[index + 1].StartsWith('-'))
        {
            var current = tokens[index + 1];
            index++;
            if (current == option && index + 1 < tokens.Count)
            {
                index++;
            }
        }

        return index;
    }

    private static int SkipTimeoutOptions(List<string> tokens, int index)
    {
        while (index + 1 < tokens.Count && tokens[index + 1].StartsWith('-'))
        {
            var option = tokens[index + 1];
            index++;
            if (option is "-s" or "-k" && index + 1 < tokens.Count)
            {
                index++;
            }
        }

        if (index + 1 < tokens.Count && !tokens[index + 1].StartsWith('/'))
        {
            index++;
        }

        return index;
    }

    private static int SkipFlockOptions(List<string> tokens, int index)
    {
        while (index + 1 < tokens.Count && tokens[index + 1].StartsWith('-'))
        {
            var option = tokens[index + 1];
            index++;
            if (option is "-w" or "-E" && index + 1 < tokens.Count)
            {
                index++;
            }
        }

        if (index + 1 < tokens.Count)
        {
            index++;
        }

        return index;
    }

    private static bool TryGetScriptPath(string token, string currentDirectory, out string scriptPath)
    {
        scriptPath = string.Empty;
        if (token.Length == 0 || token.StartsWith('-') || token.Contains('=', StringComparison.Ordinal))
        {
            return false;
        }

        if (!token.StartsWith('/') && !token.StartsWith("./", StringComparison.Ordinal) &&
            !token.StartsWith("../", StringComparison.Ordinal))
        {
            return false;
        }

        scriptPath = ResolvePath(currentDirectory, token);
        return true;
    }

    private static string ResolvePath(string currentDirectory, string token)
    {
        if (token.StartsWith('/'))
        {
            return token;
        }

        if (string.IsNullOrEmpty(currentDirectory))
        {
            return token;
        }

        var combined = currentDirectory.TrimEnd('/') + "/" + token;
        return NormalizePath(combined);
    }

    private static string NormalizePath(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        return "/" + string.Join('/', parts);
    }

    private static string FileName(string token)
    {
        var slash = token.LastIndexOf('/');
        return slash >= 0 ? token[(slash + 1)..] : token;
    }

    private static bool IsSeparator(string token)
        => token is "&&" or "||" or ";" or "|" or "&";

    private static bool IsRedirection(string token)
        => token.StartsWith('>') || token.StartsWith('<') || token.StartsWith("2>", StringComparison.Ordinal);

    private static bool LooksLikeAssignment(string token)
    {
        var equals = token.IndexOf('=');
        if (equals <= 0)
        {
            return false;
        }

        for (var i = 0; i < equals; i++)
        {
            if (!char.IsLetterOrDigit(token[i]) && token[i] != '_')
            {
                return false;
            }
        }

        return !char.IsDigit(token[0]);
    }

    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var buffer = new System.Text.StringBuilder();
        var singleQuote = false;
        var doubleQuote = false;
        var escape = false;

        void Flush()
        {
            if (buffer.Length > 0)
            {
                tokens.Add(buffer.ToString());
                buffer.Clear();
            }
        }

        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (escape)
            {
                buffer.Append(ch);
                escape = false;
                continue;
            }

            if (ch == '\\' && !singleQuote)
            {
                escape = true;
                continue;
            }

            if (ch == '\'' && !doubleQuote)
            {
                singleQuote = !singleQuote;
                continue;
            }

            if (ch == '"' && !singleQuote)
            {
                doubleQuote = !doubleQuote;
                continue;
            }

            if (!singleQuote && !doubleQuote && char.IsWhiteSpace(ch))
            {
                Flush();
                continue;
            }

            if (!singleQuote && !doubleQuote && (ch == ';' || ch == '|' || ch == '&' || ch == '>'))
            {
                Flush();
                var separator = ch.ToString();
                if (i + 1 < command.Length && command[i + 1] == ch && ch != ';' && ch != '&')
                {
                    separator += ch;
                    i++;
                }

                tokens.Add(separator);
                continue;
            }

            buffer.Append(ch);
        }

        Flush();
        return tokens;
    }
}
