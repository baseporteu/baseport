using System.Net.Sockets;
using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;

namespace Baseport.Tests;

public class StartupFailureTests
{
    [Fact]
    public void Address_in_use_names_the_address_and_suggests_a_different_port()
    {
        var inner = new SocketException((int)SocketError.AddressAlreadyInUse);
        var outer = new IOException("Failed to bind to address http://127.0.0.1:5000: address already in use.", inner);

        var message = StartupFailure.Describe(outer);

        Assert.NotNull(message);
        Assert.Contains("http://127.0.0.1:5000", message);
        Assert.Contains("--urls", message);
    }

    [Fact]
    public void Permission_denied_binding_names_the_address_and_the_port_rule()
    {
        var inner = new SocketException((int)SocketError.AccessDenied);
        var outer = new IOException("Failed to bind to address http://0.0.0.0:80: access denied.", inner);

        var message = StartupFailure.Describe(outer);

        Assert.NotNull(message);
        Assert.Contains("http://0.0.0.0:80", message);
        Assert.Contains("elevated privileges", message);
    }

    [Fact]
    public void Address_not_available_names_the_address()
    {
        var inner = new SocketException((int)SocketError.AddressNotAvailable);
        var outer = new IOException("Failed to bind to address http://10.0.0.1:5000: address not available.", inner);

        var message = StartupFailure.Describe(outer);

        Assert.NotNull(message);
        Assert.Contains("http://10.0.0.1:5000", message);
    }

    [Fact]
    public void Missing_database_file_message_passes_through_unchanged()
    {
        var ex = new InvalidOperationException("Please delete the database file and restart.");

        Assert.Equal("Please delete the database file and restart.", StartupFailure.Describe(ex));
    }

    [Fact]
    public void Sqlite_cannot_open_reports_a_path_problem()
    {
        var ex = new SqliteException("unable to open database file", 14);

        var message = StartupFailure.Describe(ex);

        Assert.NotNull(message);
        Assert.Contains("Baseport:ConnectionString", message);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    public void Sqlite_locked_or_readonly_reports_contention(int sqliteErrorCode)
    {
        var ex = new SqliteException("database is locked", sqliteErrorCode);

        var message = StartupFailure.Describe(ex);

        Assert.NotNull(message);
        Assert.Contains("locked or read-only", message);
    }

    [Fact]
    public void Unauthorized_access_names_the_working_directory_requirement()
    {
        var ex = new UnauthorizedAccessException("Access to the path is denied.");

        var message = StartupFailure.Describe(ex);

        Assert.NotNull(message);
        Assert.Contains("Access to the path is denied.", message);
        Assert.Contains("working directory", message);
    }

    [Fact]
    public void Missing_json_config_file_names_the_file()
    {
        var ex = new FileNotFoundException("Could not find file.", "appsettings.json");

        var message = StartupFailure.Describe(ex);

        Assert.NotNull(message);
        Assert.Contains("appsettings.json", message);
    }

    [Fact]
    public void An_unrecognized_exception_is_not_described()
    {
        Assert.Null(StartupFailure.Describe(new Exception("something else entirely")));
    }

    [Fact]
    public void A_matching_cause_wrapped_deep_in_inner_exceptions_is_still_found()
    {
        var root = new SqliteException("database is locked", 5);
        var middle = new InvalidOperationException("wrapped once", root);
        var outer = new Exception("wrapped twice", middle);

        var message = StartupFailure.Describe(outer);

        Assert.NotNull(message);
        Assert.Contains("locked or read-only", message);
    }
}
