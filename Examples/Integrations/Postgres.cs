#:package Npgsql@9.*

using Npgsql;

var token = args.FirstOrDefault() ?? Environment.GetEnvironmentVariable("BASEPORT_TOKEN")
    ?? throw new InvalidOperationException("Pass an API token: dotnet run Postgres.cs <token>");

var connectionString = new NpgsqlConnectionStringBuilder
{
    Host = Environment.GetEnvironmentVariable("BASEPORT_HOST") ?? "127.0.0.1",
    Port = int.Parse(Environment.GetEnvironmentVariable("BASEPORT_PG_PORT") ?? "5432"),
    Username = Environment.GetEnvironmentVariable("BASEPORT_USER") ?? "admin",
    Password = token,
    Database = "baseport",
    SslMode = SslMode.Disable,
    Pooling = false,
}.ToString();

var builder = new NpgsqlDataSourceBuilder(connectionString);
builder.ConfigureTypeLoading(o => o.EnableTypeLoading(false));

await using var source = builder.Build();
await using var connection = await source.OpenConnectionAsync();

await using var command = new NpgsqlCommand("SELECT * FROM \"Customers\" LIMIT 10", connection);
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
