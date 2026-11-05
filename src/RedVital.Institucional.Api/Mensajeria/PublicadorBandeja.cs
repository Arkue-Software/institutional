// RedVital — institutional-service. Publicador de la bandeja de salida (ADR-018,
// DD 5.3.6, plantilla .NET): cada segundo lee hasta cien eventos pendientes en
// orden de secuencia y marca publicado solo lo que el broker confirmó. Si un
// evento falla, el lote se detiene en él para no romper el orden de su clave.
// Un bloqueo consultivo de la base deja un solo publicador activo.

using System.Text;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using RedVital.Institucional.Infraestructura.Persistencia;

namespace RedVital.Institucional.Api.Mensajeria;

public sealed class PublicadorBandeja : BackgroundService
{
    private const long ClaveBloqueo = 0x52564E42; // "RVNB"
    private const int Lote = 100;

    private readonly IServiceScopeFactory _ambitos;
    private readonly ProductorKafka _kafka;
    private readonly ILogger<PublicadorBandeja> _log;

    public PublicadorBandeja(IServiceScopeFactory ambitos, ProductorKafka kafka, ILogger<PublicadorBandeja> log)
    {
        _ambitos = ambitos;
        _kafka = kafka;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CicloAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning("Falló el ciclo del publicador; se reintentará: {Mensaje}", e.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task CicloAsync(CancellationToken ct)
    {
        using var ambito = _ambitos.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<InstitucionalDbContext>();
        await using var transaccion = await contexto.Database.BeginTransactionAsync(ct);

        var bloqueado = await contexto.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({ClaveBloqueo}) AS \"Value\"")
            .SingleAsync(ct);
        if (!bloqueado) return;

        var pendientes = await contexto.EventosSalida
            .FromSqlRaw($"SELECT * FROM evento_salida WHERE estado = 'pendiente' ORDER BY secuencia LIMIT {Lote}")
            .ToListAsync(ct);

        foreach (var evento in pendientes)
        {
            var mensaje = new Message<string, string>
            {
                Key = evento.Clave,
                Value = evento.Carga,
                Headers = new Headers
                {
                    { "evento-id", Encoding.UTF8.GetBytes(evento.Id.ToString()) },
                    { "evento-tipo", Encoding.UTF8.GetBytes(evento.Tipo) },
                    { "evento-version", Encoding.UTF8.GetBytes(evento.VersionEsquema.ToString()) },
                    { "correlacion-id", Encoding.UTF8.GetBytes(evento.CorrelacionId) },
                    { "ocurrido-en", Encoding.UTF8.GetBytes(evento.CreadoEn.ToString("O")) },
                    { "productor", Encoding.UTF8.GetBytes("institucional") },
                },
            };
            try
            {
                await _kafka.Productor.ProduceAsync(evento.Tema, mensaje, ct);
            }
            catch (ProduceException<string, string> e)
            {
                evento.IntentosPublicacion++;
                _log.LogWarning("El broker no confirmó el evento {Id}; se reintentará: {Error}", evento.Id, e.Error.Reason);
                break;
            }
            var ahora = DateTimeOffset.UtcNow;
            evento.Estado = "publicado";
            evento.PublicadoEn = ahora;
            evento.ExpiraEn = ahora.AddDays(evento.ClaveNatural is null ? 7 : 30);
        }

        await contexto.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
    }
}
