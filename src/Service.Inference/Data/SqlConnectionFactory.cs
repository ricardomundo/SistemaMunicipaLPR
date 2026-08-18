using System;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Service.Inference.Data;

/// <summary>
/// Los consumers de este servicio usan Dapper directo sobre ADO.NET (no EF Core /
/// LprDbContext, que vive en Api.Web) — mantiene mínimo el footprint de dependencias de este
/// servicio de camino caliente.
/// </summary>
public class SqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("SistemaLPR")
            ?? throw new InvalidOperationException("Falta la connection string 'SistemaLPR' en la configuración.");
    }

    public IDbConnection Create() => new SqlConnection(_connectionString);
}
