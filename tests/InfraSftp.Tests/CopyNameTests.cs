using InfraSftp.ViewModels;

namespace InfraSftp.Tests;

// Covers the "- Copia" anti-cascade rule used by SuggestCopyNameAsync. The
// regex is the single source of truth that prevents runaway names like
// "report - Copia - Copia - Copia.txt" when a user pastes the same item over
// and over into the same folder.
public class CopyNameTests
{
    [Fact]
    public void Strips_Plain_Copia_Suffix()
    {
        Assert.Equal("report", MainWindowViewModel.StripCopySuffix("report - Copia"));
    }

    [Fact]
    public void Strips_Numbered_Copia_Suffix()
    {
        Assert.Equal("report", MainWindowViewModel.StripCopySuffix("report - Copia (3)"));
    }

    [Fact]
    public void Is_Case_Insensitive()
    {
        Assert.Equal("Report", MainWindowViewModel.StripCopySuffix("Report - copia"));
        Assert.Equal("Report", MainWindowViewModel.StripCopySuffix("Report - COPIA (12)"));
    }

    [Fact]
    public void Leaves_Unrelated_Suffix_Untouched()
    {
        Assert.Equal("report - draft", MainWindowViewModel.StripCopySuffix("report - draft"));
        Assert.Equal("Copia de algo", MainWindowViewModel.StripCopySuffix("Copia de algo"));
        Assert.Equal("a-Copia", MainWindowViewModel.StripCopySuffix("a-Copia"));
    }

    [Fact]
    public void Strips_Only_Trailing_Suffix_Not_Embedded()
    {
        Assert.Equal("report - Copia internal", MainWindowViewModel.StripCopySuffix("report - Copia internal"));
    }

    [Fact]
    public void Idempotent_When_Already_Stripped()
    {
        var stem = "annual-report";
        Assert.Equal(stem, MainWindowViewModel.StripCopySuffix(stem));
    }

    [Fact]
    public void Empty_Input_Returns_Empty()
    {
        Assert.Equal("", MainWindowViewModel.StripCopySuffix(""));
    }
}
