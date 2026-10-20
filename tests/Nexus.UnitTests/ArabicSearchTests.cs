using Nexus.Application.Catalogue;

namespace Nexus.UnitTests;

public class ArabicSearchTests
{
    // The variation a reader types past without thinking. Each of these must
    // find "المحفظة الصبورة" (CAT-10).
    [Theory]
    [InlineData("المحفظة الصبورة")]   // as stored
    [InlineData("المحفظه الصبوره")]   // taa marbuta typed as haa
    [InlineData("المحفظة  الصبورة")]  // doubled space
    [InlineData("  المحفظة الصبورة ")] // padded
    public void Folds_to_the_same_form(string typed) =>
        Assert.Equal(ArabicSearch.Fold("المحفظة الصبورة"), ArabicSearch.Fold(typed));

    [Theory]
    [InlineData("أحمد", "احمد")]  // alef with hamza above
    [InlineData("إسلام", "اسلام")] // alef with hamza below
    [InlineData("آمال", "امال")]  // alef madda
    [InlineData("على", "علي")]    // alef maqsura vs yaa
    [InlineData("مُحَمَّد", "محمد")]  // harakat stripped
    public void Folds_orthographic_variants(string a, string b) =>
        Assert.Equal(ArabicSearch.Fold(b), ArabicSearch.Fold(a));

    [Fact]
    public void Folds_arabic_indic_digits_to_western() =>
        Assert.Equal("2025", ArabicSearch.Fold("٢٠٢٥"));

    [Fact]
    public void English_folding_is_case_insensitive() =>
        Assert.Equal(ArabicSearch.Fold("Patient Portfolio"), ArabicSearch.Fold("pATIENT portfolio"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_folds_to_empty(string? input) =>
        Assert.Equal("", ArabicSearch.Fold(input));

    [Fact]
    public void Contains_matches_a_folded_substring() =>
        Assert.True(ArabicSearch.Contains("المحفظة الصبورة", ArabicSearch.Fold("الصبوره")));

    [Fact]
    public void Contains_rejects_a_word_that_is_not_there() =>
        Assert.False(ArabicSearch.Contains("المحفظة الصبورة", ArabicSearch.Fold("الصكوك")));

    [Fact]
    public void An_empty_needle_matches_everything() =>
        Assert.True(ArabicSearch.Contains("anything at all", ArabicSearch.Fold("")));
}
