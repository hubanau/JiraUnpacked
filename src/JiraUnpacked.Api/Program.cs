using JiraUnpacked.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// EF Core (Npgsql) + AppDbContext. Connection string resolved per environment;
// locally it points at the docker-compose Postgres (see README).
builder.Services.AddInfrastructure(builder.Configuration.GetConnectionString("Default"));

// Controllers and OpenAPI. Feature endpoints (identity, connections, reports)
// are added in later phases.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Exposed so the test project can drive the API with WebApplicationFactory<Program>
// (needed for the FR-B2 tenant-isolation integration test in a later phase).
public partial class Program { }
