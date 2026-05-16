using Npgsql;

namespace ThanyMarcus.Portal.Api.Infrastructure.Database;

public sealed class NpgsqlConnectionFactory(IConfiguration config)
{
    private readonly string _connectionString = config.GetConnectionString("Portal")
        ?? throw new InvalidOperationException("ConnectionStrings:Portal not configured");

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
