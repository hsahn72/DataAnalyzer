using DataAnalyzer.Core.Data;

namespace DataAnalyzer.Core.Tests;

public class CsvParserTests
{
    [Fact]
    public void Picks_delimiter_with_most_columns()
    {
        var r = CsvParser.Parse("t;a;b\n0;1,5;2\n1;3;4\n");
        Assert.Equal(new[] { "t", "a", "b" }, r.Names);
        Assert.True(double.IsNaN(r.Columns[1][0])); // "1,5"는 숫자가 아님 (소수점은 '.')
        Assert.Equal(3, r.Columns[1][1]);
    }

    [Fact]
    public void Reads_units_from_name_brackets_and_unit_row()
    {
        var r = CsvParser.Parse("﻿Time [s],P,Q\n,bar,(L/min)\n0,1,2\n0.1,3,4");
        Assert.True(r.HasUnitRow);
        Assert.Equal(new[] { "s", "bar", "L/min" }, r.Units);
        Assert.Equal(2, r.RowCount);
        Assert.Equal(0.1, r.Columns[0][1]);
    }

    [Fact]
    public void Second_row_with_numbers_is_data_not_units()
    {
        var r = CsvParser.Parse("t,a\n0,x\n1,2");
        Assert.False(r.HasUnitRow);
        Assert.Equal(2, r.RowCount);
    }

    [Fact]
    public void Strips_quotes_and_blank_lines()
    {
        var r = CsvParser.Parse("\"t\",\"a\"\r\n\r\n\"0\",\"5\"\r\n");
        Assert.Equal(new[] { "t", "a" }, r.Names);
        Assert.Equal(5, r.Columns[1][0]);
    }

    [Fact]
    public void Action_text_becomes_codes()
    {
        var r = CsvParser.Parse("t,Action\n0,Dig\n1,haul\n2,\n3,덤프\n4,Return\n5,Idle\n6,Swing\n7,Idle");
        var a = r.Columns[1];
        Assert.Equal(new double[] { 1, 2, double.NaN, 3, 4, 5, 6, 5 }, a);
        Assert.Equal("Idle", r.Categories["Action"][5]);
        Assert.Equal("Dig", r.Categories["Action"][1]);
    }

    [Fact]
    public void Numeric_action_column_has_no_categories()
    {
        var r = CsvParser.Parse("t,Action\n0,1\n1,2");
        Assert.Empty(r.Categories);
    }

    [Fact]
    public void Date_column_is_epoch_ms()
    {
        var r = CsvParser.Parse("time,a\n2026-01-02 03:04:05.250,1\n2026-01-02 03:04:06,2");
        Assert.True(r.IsDate[0]);
        Assert.False(r.IsDate[1]);
        Assert.Equal(750, r.Columns[0][1] - r.Columns[0][0], 6);
        var local = new DateTime(2026, 1, 2, 3, 4, 5, 250, DateTimeKind.Local);
        Assert.Equal((local.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds, r.Columns[0][0], 6);
    }
}
