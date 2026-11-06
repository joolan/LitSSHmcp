using System.Security.Cryptography;

namespace LitSSHmcp.App.Services.Toolbox;

public sealed class PasswordGeneratorOptions
{
    public int Length { get; set; } = 20;
    public bool IncludeUpper { get; set; } = true;
    public bool IncludeLower { get; set; } = true;
    public bool IncludeDigits { get; set; } = true;
    public bool IncludeSymbols { get; set; } = true;
    public bool SafeSymbolsOnly { get; set; }
    public bool ExcludeAmbiguous { get; set; } = true;
}

public sealed record GeneratedPassword(string Value, double EntropyBits, string StrengthText);

/// <summary>使用密码学安全随机数生成密码，并保证每个勾选的字符集至少出现一次。</summary>
public static class RandomPasswordGenerator
{
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Digits = "0123456789";
    private const string Symbols = "!@#$%^&*()-_=+[]{};:,.<>?/";
    private const string SafeSymbols = "_-";
    private const string Ambiguous = "O0oIl1|`'\"";

    public static GeneratedPassword Generate(PasswordGeneratorOptions options)
    {
        var length = Math.Clamp(options.Length, 4, 128);
        var sets = new List<string>();
        if (options.IncludeUpper) sets.Add(Filter(Upper, options.ExcludeAmbiguous));
        if (options.IncludeLower) sets.Add(Filter(Lower, options.ExcludeAmbiguous));
        if (options.IncludeDigits) sets.Add(Filter(Digits, options.ExcludeAmbiguous));
        if (options.IncludeSymbols)
            sets.Add(options.SafeSymbolsOnly ? SafeSymbols : Filter(Symbols, options.ExcludeAmbiguous));

        if (sets.Count == 0)
            throw new ArgumentException("至少选择一种字符类型。", nameof(options));
        if (length < sets.Count)
            throw new ArgumentException("密码长度不能小于已选字符类型数量。", nameof(options));

        var pool = string.Concat(sets);
        var chars = new List<char>(length);
        foreach (var set in sets)
            chars.Add(set[RandomNumberGenerator.GetInt32(set.Length)]);

        while (chars.Count < length)
            chars.Add(pool[RandomNumberGenerator.GetInt32(pool.Length)]);

        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        var value = new string(chars.ToArray());
        var entropy = length * Math.Log2(pool.Length);
        return new GeneratedPassword(value, entropy, StrengthFor(entropy));
    }

    private static string Filter(string source, bool excludeAmbiguous)
    {
        if (!excludeAmbiguous)
            return source;
        return new string(source.Where(c => !Ambiguous.Contains(c)).ToArray());
    }

    private static string StrengthFor(double bits) => bits switch
    {
        >= 120 => "很强",
        >= 80 => "强",
        >= 50 => "中等",
        _ => "弱"
    };
}
