using VmbLauncher.Views;

namespace VmbLauncher.Tests;

public class NewModNameValidationTests
{
    [Theory]
    [InlineData("MyMod")]
    [InlineData("my_first_mod")]
    [InlineData("test1")]
    [InlineData("ab")]
    [InlineData("Z9")]
    [InlineData("snake_case_with_99_digits")]
    public void ValidateName_accepts_legal_names(string name)
    {
        Assert.Null(NewModWindow.ValidateName(name));
    }

    [Fact]
    public void ValidateName_rejects_empty()
    {
        Assert.NotNull(NewModWindow.ValidateName(""));
        Assert.NotNull(NewModWindow.ValidateName("   "));
    }

    [Fact]
    public void ValidateName_rejects_single_char()
    {
        Assert.NotNull(NewModWindow.ValidateName("a"));
    }

    [Fact]
    public void ValidateName_rejects_starting_with_digit()
    {
        var err = NewModWindow.ValidateName("9mod");
        Assert.NotNull(err);
        Assert.Contains("letter", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateName_rejects_starting_with_underscore()
    {
        Assert.NotNull(NewModWindow.ValidateName("_mod"));
    }

    [Theory]
    [InlineData("my mod")]    // space
    [InlineData("my-mod")]    // hyphen
    [InlineData("my.mod")]    // dot
    [InlineData("my/mod")]    // slash
    [InlineData("my\\mod")]   // backslash
    [InlineData("my!mod")]    // bang
    [InlineData("my(mod)")]   // parens
    public void ValidateName_rejects_illegal_chars(string name)
    {
        var err = NewModWindow.ValidateName(name);
        Assert.NotNull(err);
        Assert.Contains("letters, digits, and underscores", err);
    }

    [Fact]
    public void ValidateName_rejects_too_long()
    {
        var name = "a" + new string('b', 64); // 65 chars total
        Assert.NotNull(NewModWindow.ValidateName(name));
    }

    [Fact]
    public void ValidateName_accepts_exactly_64_chars()
    {
        var name = "a" + new string('b', 63); // 64 chars
        Assert.Null(NewModWindow.ValidateName(name));
    }

    [Fact]
    public void ValidateName_trims_whitespace_before_validating()
    {
        Assert.Null(NewModWindow.ValidateName("  MyMod  "));
    }
}
