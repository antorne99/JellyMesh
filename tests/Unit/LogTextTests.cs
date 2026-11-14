using System.Linq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests;

public class LogTextTests
{
    [Theory]
    [InlineData("Film\nForged line", "FilmForged line")]
    [InlineData("Film\r\nForged", "FilmForged")]
    [InlineData("Film\rForged", "FilmForged")]
    [InlineData("Film\tdi casa", "Filmdi casa")]
    [InlineData("A\0B\u0007C\u001bD\u007fE", "ABCDE")]
    [InlineData("A\u0085B\u009bC", "ABC")]
    [InlineData("A\u2028B\u2029C", "ABC")]
    [InlineData("\n\r\t", "")]
    public void RemovesControlCharactersAndLineSeparators(string value, string expected)
    {
        Assert.Equal(expected, LogText.Clean(value));
    }

    [Theory]
    [InlineData("A​B‌C‍D‎E‏F", "ABCDEF")]
    [InlineData("A‪B‫C‬D‭E‮F", "ABCDEF")]
    [InlineData("A⁦B⁧C⁨D⁩E", "ABCDE")]
    [InlineData("﻿Film", "Film")]
    [InlineData("A؜B⁠C⁡D⁢E⁣F⁤G", "ABCDEFG")]
    [InlineData("A؛B C", "A؛B C")]
    [InlineData("A ‐⁥⁪B", "A ‐⁥⁪B")]
    public void RemovesBidiAndZeroWidthCharacters(string value, string expected)
    {
        Assert.Equal(expected, LogText.Clean(value));
    }

    [Fact]
    public void KeepsANameOf200CharactersAsTheSameString()
    {
        var name = new string('a', 200);

        Assert.Same(name, LogText.Clean(name));
    }

    [Fact]
    public void CutsALongerNameAt200CharactersWithAnEllipsis()
    {
        var name = new string('a', 200) + "bcd";

        Assert.Equal(new string('a', 200) + "…", LogText.Clean(name));
    }

    [Fact]
    public void DoesNotCutASurrogatePairInTwo()
    {
        var name = new string('a', 199) + "😀" + "b";

        Assert.Equal(new string('a', 199) + "…", LogText.Clean(name));
    }

    [Fact]
    public void KeepsASurrogatePairThatEndsAt200()
    {
        var name = new string('a', 198) + "😀" + "b";

        Assert.Equal(new string('a', 198) + "😀…", LogText.Clean(name));
    }

    [Fact]
    public void CountsTheLengthAfterRemovingCharacters()
    {
        var name = new string('a', 100) + "​\n‮" + new string('b', 100);

        Assert.Equal(new string('a', 100) + new string('b', 100), LogText.Clean(name));
    }

    [Fact]
    public void CutsALongNameAfterRemovingCharacters()
    {
        var name = string.Concat(Enumerable.Repeat("ab​", 150));

        Assert.Equal(string.Concat(Enumerable.Repeat("ab", 100)) + "…", LogText.Clean(name));
    }

    [Fact]
    public void CleanMessageRemovesCharactersButDoesNotCut()
    {
        var message = new string('a', 300) + "\n‮" + "b";

        Assert.Equal(new string('a', 300) + "b", LogText.CleanMessage(message));
    }

    [Fact]
    public void KeepsAPlainNameAsTheSameString()
    {
        const string Name = "Film di casa – Ünïcode 映画";

        Assert.Same(Name, LogText.Clean(Name));
    }

    [Fact]
    public void KeepsNull()
    {
        Assert.Null(LogText.Clean(null));
    }
}
