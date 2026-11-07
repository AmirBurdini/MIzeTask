using Dapper;
using SupplierFeed.Api.Configuration;
using SupplierFeed.Api.Data;
using SupplierFeed.Api.Infrastructure;
using SupplierFeed.Api.Ingest;
using SupplierFeed.Api.Stats;

var builder = WebApplication.CreateBuilder(args);

// Map snake_case columns (e.g. updated_at_utc) onto POCO properties.
DefaultTypeMap.MatchNamesWithUnderscores = true;

builder.Services.AddControllers();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IngestRequestParser>();
builder.Services.AddSingleton<IngestRequestValidator>();
builder.Services.AddSingleton<ThrottleLog>();
builder.Services.AddSingleton<ReservationStore>();
builder.Services.AddSingleton<StatsCounters>();
builder.Services.AddSingleton<IIngestService, IngestService>();
builder.Services.AddSingleton<IStatsQuery, StatsQuery>();

var app = builder.Build();

SchemaInitializer.Initialize(app.Services.GetRequiredService<ISqliteConnectionFactory>());

app.UseExceptionHandler();

app.MapControllers();

app.Run();

// Exposed so integration tests can host the app with WebApplicationFactory.
public partial class Program { }
