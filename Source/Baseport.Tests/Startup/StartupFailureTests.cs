using System.Net.Sockets;
using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;

namespace Baseport.Tests;

public class StartupFailureTests
{
    [Fact]
    public void AddressInUse()
    {
        var inner = new SocketException((int)SocketError.AddressAlreadyInUse);
        var outer = new IOException("Failed to bind to address http://127.0.0.1:5000: address already in use.", inner);

        var message = StartupFailure.Describe(outer);

        Assert.NotNull(message);
        Assert.Contains("http://127.0.0.1:5000", message);
        Assert.Contains("--urls", message);
    }

    [Fact]
    public void PermissionDenied()
    {
        var inner = new SocketException((int)SocketError.AccessDenied);
        var outer = new IOException("Failed to bind to address http://0.0.0.0:80: access denied.", inner);

        var message = StartupFailure.Describe(outer);

        Assert.NotNull(message);
        Assert.Contains("http://0.0.0.0:80", message);
        Assert.Contains("elevated privileges", message);
    }

    [Fact]
    public void AddressNotAvailable()
    {
        var inner = new SocketException((int)SocketError.AddressNotAvailable);
        var outer = new IOException("Failed to bind to address http://10.0.0.1:5000: address not available.", inner);

        var message = StartupFailure.Describe(outer);

        Assert.NotNull(message);
        Assert.Contains("http://10.0.0.1:5000", message);
    }

    [Fact]
    public void MissingDatabasePassesThrough()
    {
        var ex = new InvalidOperationException("Please delete the database file and restart.");

        Assert.Equal("Please delete the database file and restart.", StartupFailure.Describe(ex));
    }

    [Fact]
    public void SqliteCannotOpen()
    {
        var ex = new SqliteException("unable to open database file", 14);

        var message = StartupFailure.Describe(ex);

        Assert.NotNull(message);
        Assert.Contains("Baseport:ConnectionString", message);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    public void SqliteLocked(int sqliteErrorCode)
    {
        var ex = new SqliteException("database is locked", sqliteErrorCode);

        var message = StartupFailure.Describe(ex);

        Assert.NotNull(message);
        Assert.Contains("locked or read-only", message);
    }

    [Fact]
    public void UnauthorizedAccess()
    {
        var ex = new UnauthorizedAccessException("Access to the path is denied.");

        var message = StartupFailure.Describe(ex);

        Assert.NotNull(message);
        Assert.Contains("Access to the path is denied.", message);
        Assert.Contains("working directory", message);
    }

    [Fact]
    public void MissingConfigFile()
    {
        var ex = new FileNotFoundException("Could not find file.", "appsettings.json");

        var message = StartupFailure.Describe(ex);

        Assert.NotNull(message);
        Assert.Contains("appsettings.json", message);
    }

    [Fact]
    public void UnknownExceptionNotDescribed()
    {
        Assert.Null(StartupFailure.Describe(new Exception("something else entirely")));
    }

    [Fact]
    public void NestedCauseFound()
    {
        var root = new SqliteException("database is locked", 5);
        var middle = new InvalidOperationException("wrapped once", root);
        var outer = new Exception("wrapped twice", middle);

        var message = StartupFailure.Describe(outer);

        Assert.NotNull(message);
        Assert.Contains("locked or read-only", message);
    }
}
