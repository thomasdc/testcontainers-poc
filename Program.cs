using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Diagnostics;

var logger = LoggerFactory.Create(_ =>
{
    _.AddConsole();
}).CreateLogger<Program>();

TestcontainersSettings.Logger = logger;

await RunNginx();
await RunPostgres();

async Task RunPostgres()
{
    var builder = new TestcontainersBuilder<PostgreSqlTestcontainer>()
        .WithDatabase(new PostgreSqlTestcontainerConfiguration
        {
            Database = "db",
            Username = "postgres",
            Password = "postgres",
        });

    await using var container = builder.Build();
    await container.StartAsync();
    await using var connection = new NpgsqlConnection(container.ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand("SELECT 1", connection);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    var res = reader.GetInt32(0);
    logger.LogInformation("Got {Result} from postgres", res);
    Debug.Assert(res == 1);
}

async Task RunNginx()
{
    var builder = new TestcontainersBuilder<TestcontainersContainer>()
      .WithImage("nginx")
      .WithName("nginx")
      .WithPortBinding(80)
      .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(80));

    await using var container = builder.Build();
    await container.StartAsync();
    var res = await new HttpClient().GetAsync("http://localhost:80");
    logger.LogInformation("Got {Result} from nginx", res.StatusCode);
    Debug.Assert(res.IsSuccessStatusCode);
}
