using Baseport.Providers.Postgres;
using Xunit;

namespace Baseport.Tests;

// The two rewrites a jdbc client's metadata queries depend on. Both walk the statement, so the cases that matter are the ones where the pattern appears inside something that must not be touched.
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
    public void A_cast_is_stripped(string sql, string expected) =>
        Assert.Equal(expected, PostgresConnection.StripCasts(sql));

    // a value is not sql, so a :: inside one has to survive
    [Theory]
    [InlineData("SELECT * FROM t WHERE name = 'a::b'")]
    [InlineData("SELECT \"a::b\" FROM t")]
    [InlineData("SELECT * FROM t WHERE name = 'it''s a::b'")]
    public void A_cast_inside_a_literal_is_left_alone(string sql) =>
        Assert.Equal(sql, PostgresConnection.StripCasts(sql));

    [Theory]
    [InlineData("SELECT $1", "5", "SELECT 5")]
    [InlineData("SELECT $1", "5.25", "SELECT 5.25")]
    [InlineData("SELECT $1", "abc", "SELECT 'abc'")]
    [InlineData("SELECT $1", "O'Brien", "SELECT 'O''Brien'")]
    public void A_bound_parameter_becomes_a_literal(string sql, string value, string expected) =>
        Assert.Equal(expected, PostgresConnection.Inline(sql, [value]));

    [Fact]
    public void A_null_parameter_becomes_null()
        => Assert.Equal("SELECT NULL", PostgresConnection.Inline("SELECT $1", [null]));

    [Fact]
    public void Parameters_are_matched_by_position()
        => Assert.Equal("SELECT 'a', 2, 'c'", PostgresConnection.Inline("SELECT $1, $2, $3", ["a", "2", "c"]));

    // the placeholder syntax is also just text inside a value
    [Fact]
    public void A_placeholder_inside_a_literal_is_left_alone()
        => Assert.Equal("SELECT '$1', 'x'", PostgresConnection.Inline("SELECT '$1', $1", ["x"]));

    [Fact]
    public void An_unbound_placeholder_is_left_alone()
        => Assert.Equal("SELECT $2", PostgresConnection.Inline("SELECT $2", ["only-one"]));
}
