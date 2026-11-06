using LitSSHmcp.App.Services.Toolbox;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class RandomPasswordGeneratorTests
{
    [Fact]
    public void Generate_returns_requested_length_and_each_selected_character_set()
    {
        var generated = RandomPasswordGenerator.Generate(new PasswordGeneratorOptions
        {
            Length = 32,
            IncludeUpper = true,
            IncludeLower = true,
            IncludeDigits = true,
            IncludeSymbols = true,
            ExcludeAmbiguous = false
        });

        Assert.Equal(32, generated.Value.Length);
        Assert.Contains(generated.Value, char.IsUpper);
        Assert.Contains(generated.Value, char.IsLower);
        Assert.Contains(generated.Value, char.IsDigit);
        Assert.Contains(generated.Value, c => "!@#$%^&*()-_=+[]{};:,.<>?/".Contains(c));
        Assert.True(generated.EntropyBits > 0);
        Assert.False(string.IsNullOrWhiteSpace(generated.StrengthText));
    }

    [Fact]
    public void Exclude_ambiguous_removes_confusing_characters()
    {
        const string ambiguous = "O0oIl1|`'\"";
        for (var i = 0; i < 20; i++)
        {
            var generated = RandomPasswordGenerator.Generate(new PasswordGeneratorOptions
            {
                Length = 64,
                ExcludeAmbiguous = true
            });
            Assert.DoesNotContain(generated.Value, c => ambiguous.Contains(c));
        }
    }

    [Fact]
    public void Safe_symbols_only_limits_symbols_to_underscore_and_hyphen()
    {
        var generated = RandomPasswordGenerator.Generate(new PasswordGeneratorOptions
        {
            Length = 64,
            IncludeUpper = true,
            IncludeLower = true,
            IncludeDigits = true,
            IncludeSymbols = true,
            SafeSymbolsOnly = true,
            ExcludeAmbiguous = false
        });

        Assert.All(generated.Value, c => Assert.True(char.IsLetterOrDigit(c) || c is '_' or '-'));
    }

    [Fact]
    public void Generate_requires_at_least_one_character_set()
    {
        Assert.Throws<ArgumentException>(() => RandomPasswordGenerator.Generate(new PasswordGeneratorOptions
        {
            IncludeUpper = false,
            IncludeLower = false,
            IncludeDigits = false,
            IncludeSymbols = false
        }));
    }
}
