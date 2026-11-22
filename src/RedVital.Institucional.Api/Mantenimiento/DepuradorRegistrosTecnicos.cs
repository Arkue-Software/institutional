// RedVital — institutional-service. Borra los registros técnicos que ya cumplieron su
// vigencia: los eventos de la bandeja de salida que el broker confirmó, y lo
// demás que se indica abajo. Sin esto esas tablas crecerían sin límite. Corre
// cada hora; un fallo se registra y se reintenta en la siguiente vuelta, sin
// afectar al resto del servicio.

using Microsoft.EntityFrameworkCore;
using RedVital.Institucional.Infraestructura.Persistencia;

namespace RedVital.Institucional.Api.Mantenimiento;

public sealed class DepuradorRegistrosTecnicos(IServiceScopeFactory ambitos, ILogger<DepuradorRegistrosTecnicos> log)
    : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromHours(1);
    private static readonly TimeSpan EsperaInicial = TimeSpan.FromMinutes(1);

    /// <summary>Cada sentencia borra lo vencido de una tabla; ninguna toca algo pendiente.</summary>
    public static readonly IReadOnlyList<string> Depuraciones =
    [
        "DELETE FROM evento_salida WHERE estado = 'publicado' AND expira_en < now()",
        "DELETE FROM evento_procesado WHERE expira_en < now()",
    ];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(EsperaInicial, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var ambito = ambitos.CreateScope();
                    var borrados = await DepurarAsync(ambito.ServiceProvider.GetRequiredService<InstitucionalDbContext>(), ct);
                    if (borrados > 0) log.LogInformation("Depuración de registros técnicos: {Borrados} filas", borrados);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning("Falló la depuración de registros técnicos; se reintentará: {Mensaje}", e.Message);
                }
                await Task.Delay(Intervalo, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public static async Task<int> DepurarAsync(InstitucionalDbContext contexto, CancellationToken ct)
    {
        var total = 0;
        foreach (var sentencia in Depuraciones) total += await contexto.Database.ExecuteSqlRawAsync(sentencia, ct);
        return total;
    }
}
