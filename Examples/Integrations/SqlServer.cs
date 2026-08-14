#:package Microsoft.Data.SqlClient@6.*

using Microsoft.Data.SqlClient;

var token = args.FirstOrDefault() ?? Environment.GetEnvironmentVariable("BASEPORT_TOKEN")
    ?? throw new InvalidOperationException("Pass an API token: dotnet run SqlServer.cs <token>");

var host = Environment.GetEnvironmentVariable("BASEPORT_HOST") ?? "127.0.0.1";
var port = Environment.GetEnvironmentVariable("BASEPORT_TDS_PORT") ?? "1433";

var connectionString = new SqlConnectionStringBuilder
{
    DataSource = $"{host},{port}",
    UserID = Environment.GetEnvironmentVariable("BASEPORT_USER") ?? "admin",
    Password = token,
    InitialCatalog = "baseport",
    Encrypt = SqlConnectionEncryptOption.Optional,
    TrustServerCertificate = true,
    Pooling = false,
}.ToString();

await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();

await using var command = new SqlCommand("SELECT TOP 10 * FROM Customers", connection);
await using var reader = await command.ExecuteReaderAsync();

var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
Console.WriteLine(string.Join(" | ", columns));
Console.WriteLine(new string('-', 60));

var count = 0;
while (await reader.ReadAsync())
{
    var values = Enumerable.Range(0, reader.FieldCount)
        .Select(i => reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString());
    Console.WriteLine(string.Join(" | ", values));
    count++;
}

Console.WriteLine($"\n{count} row(s).");
