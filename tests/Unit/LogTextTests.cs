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
    [InlineData("A ‐⁥⁪B", "A ‐⁥⁪B")]
    public void RemovesBidiAndZeroWidthCharacters(string value, string expected)
    {
        Assert.Equal(expected, LogText.Clean(value));
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
