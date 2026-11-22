// PostgreSQL real con los roles y las migraciones del repositorio de bases
// (BDs_RedVital/databases/institucional). El servicio se conecta como
// institucional_servicio: las pruebas también prueban sus permisos por columna.

using Microsoft.EntityFrameworkCore;
using Npgsql;
using RedVital.Institucional.Infraestructura.Persistencia;
using Testcontainers.PostgreSql;
using Xunit;

namespace RedVital.Institucional.Pruebas;

public sealed class BaseInstitucional : IAsyncLifetime
{
    private const string ClaveServicio = "servicio-prueba";
    private const string ClavePropietario = "propietario-prueba";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16").WithDatabase("db_institucional").Build();

    public string CadenaServicio { get; private set; } = "";
    public string CadenaPropietario { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var dir = Path.GetFullPath(Environment.GetEnvironmentVariable("REDVITAL_MIGRACIONES_INSTITUCIONAL")
            ?? Path.Combine(AppContext.BaseDirectory, "../../../../../../../../BDs_RedVital/databases/institucional"));
        await _postgres.CopyAsync(await File.ReadAllBytesAsync(Path.Combine(dir, "00_roles.sql")), "/tmp/00_roles.sql");
        var roles = await _postgres.ExecAsync(["psql", "-U", "postgres", "-d", "db_institucional",
            "-v", $"password_propietario={ClavePropietario}", "-v", $"password_servicio={ClaveServicio}", "-f", "/tmp/00_roles.sql"]);
        Assert.True(roles.ExitCode == 0, roles.Stderr);

        CadenaPropietario = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
            { Username = "institucional_propietario", Password = ClavePropietario }.ConnectionString;
        await using (var conexion = new NpgsqlConnection(CadenaPropietario))
        {
            await conexion.OpenAsync();
            foreach (var archivo in Directory.GetFiles(Path.Combine(dir, "migraciones"), "V*.sql").Order())
            {
                await using var comando = new NpgsqlCommand(await File.ReadAllTextAsync(archivo), conexion);
                await comando.ExecuteNonQueryAsync();
            }
        }
        CadenaServicio = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
            { Username = "institucional_servicio", Password = ClaveServicio }.ConnectionString;
    }

    public InstitucionalDbContext Contexto() =>
        new(new DbContextOptionsBuilder<InstitucionalDbContext>().UseNpgsql(CadenaServicio).Options);

    public async Task<T> EscalarAsync<T>(string sql)
    {
        await using var conexion = new NpgsqlConnection(CadenaServicio);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        return (T)(await comando.ExecuteScalarAsync())!;
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();
}

[CollectionDefinition(Nombre)]
public sealed class ColeccionBase : ICollectionFixture<BaseInstitucional>
{
    public const string Nombre = "db_institucional";
}
