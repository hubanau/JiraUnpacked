using JiraUnpacked.Infrastructure.Reporting;

namespace JiraUnpacked.Tests.Reporting;

public sealed class CsvWriterTests
{
    [Theory]
    [InlineData("=1+1", "\"'=1+1\"")]
    [InlineData("+1", "\"'+1\"")]
    [InlineData("-1", "\"'-1\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("\tlead", "\"'\tlead\"")]
    public void A_leading_formula_character_is_neutralised(string input, string expected)
    {
        // Excel and Sheets execute a cell beginning with these. A Jira summary is text a stranger
        // can write, so exporting someone else's issues would otherwise run their formula on the
        // machine of whoever opens the file.
        Assert.Equal(expected, CsvWriter.Escape(input));
    }

    [Fact]
    public void A_formula_containing_quotes_is_both_neutralised_and_quoted()
    {
        // The two defences compose: the apostrophe stops execution, the doubling keeps the CSV
        // parseable. Getting only one right leaves either an exploit or a corrupt file.
        Assert.Equal("\"'=HYPERLINK(\"\"x\"\")\"", CsvWriter.Escape("=HYPERLINK(\"x\")"));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has\"quote", "\"has\"\"quote\"")]
    [InlineData("has\nnewline", "\"has\nnewline\"")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Ordinary_values_follow_RFC_4180(string? input, string expected)
        => Assert.Equal(expected, CsvWriter.Escape(input));

    [Fact]
    public void A_minus_sign_inside_a_value_is_left_alone()
    {
        // Only a LEADING character makes a formula. Prefixing every hyphen would mangle dates
        // and hyphenated keys for no gain.
        Assert.Equal("ACME-1", CsvWriter.Escape("ACME-1"));
        Assert.Equal("2026-01-01", CsvWriter.Escape("2026-01-01"));
    }

    [Fact]
    public void A_row_joins_cells_and_terminates_as_RFC_4180_requires()
        => Assert.Equal("a,\"b,c\",\r\n", CsvWriter.Row(new[] { "a", "b,c", null }));
}
