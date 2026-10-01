using Baseport.Providers.Postgres;
using Xunit;

namespace Baseport.Tests;

public class PostgresRewriteTests
{
    [Theory]
    [InlineData("SELECT 'pg_namespace'::regclass", "SELECT 'pg_namespace'")]
    [InlineData("SELECT nspname::text FROM pg_namespace", "SELECT nspname FROM pg_namespace")]
    [InlineData("SELECT a::int[] FROM t", "SELECT a FROM t")]
    [InlineData("SELECT a::character varying FROM t", "SELECT a FROM t")]
    [InlineData("SELECT a::double precision FROM t", "SELECT a FROM t")]
    [InlineData("SELECT a::\"char\" FROM t", "SELECT a FROM t")]
    [InlineData("SELECT relname FROM pg_class", "SELECT relname FROM pg_class")]
    public void CastStripped(string sql, string expected) =>
        Assert.Equal(expected, PostgresConnection.StripCasts(sql));

    [Theory]
    [InlineData("SELECT * FROM t WHERE name = 'a::b'")]
    [InlineData("SELECT \"a::b\" FROM t")]
    [InlineData("SELECT * FROM t WHERE name = 'it''s a::b'")]
    public void CastInLiteralKept(string sql) =>
        Assert.Equal(sql, PostgresConnection.StripCasts(sql));

    [Theory]
    [InlineData("SELECT $1", "5", "SELECT 5")]
    [InlineData("SELECT $1", "5.25", "SELECT 5.25")]
    [InlineData("SELECT $1", "abc", "SELECT 'abc'")]
    [InlineData("SELECT $1", "O'Brien", "SELECT 'O''Brien'")]
    public void ParameterBecomesLiteral(string sql, string value, string expected) =>
        Assert.Equal(expected, PostgresConnection.Inline(sql, [value]));

    [Fact]
    public void NullParameterBecomesNull()
        => Assert.Equal("SELECT NULL", PostgresConnection.Inline("SELECT $1", [null]));

    [Fact]
    public void ParametersByPosition()
        => Assert.Equal("SELECT 'a', 2, 'c'", PostgresConnection.Inline("SELECT $1, $2, $3", ["a", "2", "c"]));

    [Fact]
    public void PlaceholderInLiteralKept()
        => Assert.Equal("SELECT '$1', 'x'", PostgresConnection.Inline("SELECT '$1', $1", ["x"]));

    [Fact]
    public void UnboundPlaceholderKept()
        => Assert.Equal("SELECT $2", PostgresConnection.Inline("SELECT $2", ["only-one"]));
}
