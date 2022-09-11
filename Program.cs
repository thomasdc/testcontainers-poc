using Bogus;
using Bogus.Distributions.Gaussian;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SolrNet;
using SolrNet.Attributes;
using SolrNet.Commands.Parameters;
using System.Diagnostics;

var logger = LoggerFactory.Create(_ => _.AddConsole()).CreateLogger<Program>();

TestcontainersSettings.Logger = logger;

await RunNginx();
await RunPostgres();
await RunSolr();

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
}

async Task RunSolr()
{
    using var consumer = Consume.RedirectStdoutAndStderrToStream(new MemoryStream(), new MemoryStream());
    var builder = new TestcontainersBuilder<TestcontainersContainer>()
                .WithName($"SampleSolrApp-Solr-{Guid.NewGuid()}")
                .WithImage("solr:8.8.2")
                .WithPortBinding(8983, assignRandomHostPort: true)
                .WithCommand("/opt/docker-solr/scripts/solr-precreate", "techproducts")
                .WithOutputConsumer(consumer)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(consumer.Stdout, "Registered new searcher"));

    await using var container = builder.Build();
    await container.StartAsync();

    var provider = new ServiceCollection()
        .AddLogging(_ => _.AddConsole())
        .AddSolrNet<Product>($"http://{container.Hostname}:{container.GetMappedPublicPort(8983)}/solr/techproducts")
        .BuildServiceProvider();

    var solr = provider.GetRequiredService<ISolrOperations<Product>>();
    var sw = Stopwatch.StartNew();
    var products = Product.Generator.Generate(100_000);
    logger.LogInformation("Products generated in {Elapsed}", sw.Elapsed);
    sw.Restart();
    await solr.AddRangeAsync(products);
    await solr.CommitAsync();
    logger.LogInformation("Products added to solr index in {Elapsed}", sw.Elapsed);
    sw.Restart();

    var query = new SolrQuery("*:*");
    var results = await solr.QueryAsync("*:*", new QueryOptions() { Rows = 0 });
    logger.LogInformation("Query executed in {Elapsed}", sw.Elapsed);
    logger.LogInformation("Got {Count} results from solr", results.NumFound);
}

public class Product
{
    [SolrUniqueKey("id")]
    public string Id { get; set; }

    [SolrField("sku")]
    public string SKU { get; set; }

    [SolrField("name")]
    public string Name { get; set; }

    [SolrField("manu_str")]
    public ICollection<string> Manufacturer { get; set; }

    [SolrField("cat")]
    public ICollection<string> Categories { get; set; }

    [SolrField("color")]
    public string Color { get; set; }

    [SolrField("price")]
    public decimal Price { get; set; }

    [SolrField("popularity")]
    public int Popularity { get; set; }

    [SolrField("inStock")]
    public bool InStock { get; set; }

    [SolrField("timestamp")]
    public DateTime Timestamp { get; set; }

    public static Faker<Product> Generator { get; } = new Faker<Product>()
        .RuleFor(p => p.Id, p => p.Random.AlphaNumeric(10))
        .RuleFor(p => p.SKU, p => p.Commerce.Ean13())
        .RuleFor(p => p.Name, p => p.Commerce.ProductName())
        .RuleFor(p => p.Manufacturer, p => new[] { p.Company.CompanyName() })
        .RuleFor(p => p.Categories, p => p.Commerce.Categories(p.Random.Number(20)))
        .RuleFor(p => p.Color, p => p.Commerce.Color())
        .RuleFor(p => p.Price, p => p.Random.GaussianDecimal(8, 2))
        .RuleFor(p => p.Popularity, p => p.Random.Number(10))
        .RuleFor(p => p.InStock, p => p.Random.Bool(.8f))
        .RuleFor(p => p.Timestamp, p => p.Date.Future());
}
