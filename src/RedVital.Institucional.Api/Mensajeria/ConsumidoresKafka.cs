// RedVital — institutional-service. Consumidores de Kafka, grupo
// institucional (ADR-018, DD 14.1): EV-09 indicators.updated y EV-10
// audit.recorded de Identidad, Campañas y Donación. Ni dejan caer al servicio
// ni se saltan un evento:
//
//  - Al menos una vez sin esperar al broker por cada mensaje: la posición se
//    guarda solo después de aplicar el evento y librdkafka la confirma en
//    segundo plano. Un duplicado tras un reinicio lo descarta evento_procesado.
//  - Fallo del mensaje: tres reintentos en pocos segundos (1, 2 y 4) y luego
//    <tema>.institucional.dlt. Un mensaje que no cumple el contrato va directo.
//  - Fallo de la base: no es culpa del evento. Se vuelve a leer el mismo
//    mensaje cada EsperaDependencia hasta que la base responda; nada va a
//    mensajes muertos por eso.
//  - Fallo del broker (no se pudo leer, guardar la posición o escribir en
//    mensajes muertos): se registra y se reintenta. El API sigue atendiendo, y
//    si el consumidor mismo queda inservible se crea uno nuevo.

using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using RedVital.Institucional.Aplicacion.CasosDeUso;
using RedVital.Institucional.Aplicacion.Puertos;
using RedVital.Institucional.Dominio;
using RedVital.Institucional.Infraestructura.Persistencia;

namespace RedVital.Institucional.Api.Mensajeria;

/// <summary>
/// Ciclo común de los consumidores de Institucional. Cada contrato tiene su
/// subclase, que solo dice qué temas lee, cómo leer la carga y qué caso de uso
/// la aplica.
/// </summary>
public abstract class ConsumidorKafka<TEvento> : BackgroundService where TEvento : class
{
    private static readonly TimeSpan[] Esperas = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    private readonly OpcionesKafka _opciones;
    private readonly ProductorKafka _kafka;
    private readonly IServiceScopeFactory _ambitos;
    private readonly ILogger _log;

    protected ConsumidorKafka(OpcionesKafka opciones, ProductorKafka kafka, IServiceScopeFactory ambitos, ILogger log)
    {
        _opciones = opciones;
        _kafka = kafka;
        _ambitos = ambitos;
        _log = log;
    }

    /// <summary>Temas que consume, todos con el mismo contrato.</summary>
    protected abstract IReadOnlyList<string> Temas { get; }

    private string Nombre => string.Join(", ", Temas);

    /// <summary>Tipo del contrato, para el motivo de un mensaje muerto.</summary>
    protected abstract string TipoEvento { get; }

    /// <summary>Nulo si la carga no cumple el contrato.</summary>
    protected abstract TEvento? Interpretar(string carga);

    /// <returns>Falso si el evento ya se había aplicado.</returns>
    protected abstract Task<bool> AplicarAsync(IServiceProvider servicios, string tema, Guid eventoId, TEvento evento, CancellationToken ct);

    protected override Task ExecuteAsync(CancellationToken ct) => Task.Run(() => SupervisarAsync(ct), ct);

    /// <summary>Si el consumidor falla de forma inesperada, se crea otro: nunca se detiene el servicio.</summary>
    private async Task SupervisarAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConsumirAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.LogError(e, "El consumidor de {Temas} falló; se crea otro en {Espera}", Nombre,
                    _opciones.EsperaDependencia);
                await EsperarAsync(_opciones.EsperaDependencia, ct);
            }
        }
    }

    private async Task ConsumirAsync(CancellationToken ct)
    {
        using var consumidor = new ConsumerBuilder<string, string>(_opciones.Aplicar(new ConsumerConfig
        {
            GroupId = TemasInstitucional.Grupo,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
            AutoCommitIntervalMs = 1000,
            ClientId = "institucional-" + typeof(TEvento).Name,
        }))
            .SetErrorHandler((_, e) => _log.LogWarning("Kafka: {Motivo}", e.Reason))
            .Build();
        consumidor.Subscribe(Temas);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<string, string>? registro;
                try
                {
                    registro = consumidor.Consume(TimeSpan.FromSeconds(1));
                }
                catch (ConsumeException e) when (!e.Error.IsFatal)
                {
                    _log.LogWarning("No fue posible leer del broker: {Error}", e.Error.Reason);
                    await EsperarAsync(TimeSpan.FromSeconds(2), ct);
                    continue;
                }
                if (registro is null) continue;

                if (await ProcesarAsync(registro, ct))
                    GuardarPosicion(consumidor, registro);
                else
                    VolverALeer(consumidor, registro);
            }
        }
        finally
        {
            try
            {
                consumidor.Close(); // confirma lo ya guardado y deja el grupo
            }
            catch (KafkaException e)
            {
                _log.LogWarning("No fue posible cerrar el consumidor limpiamente: {Error}", e.Error.Reason);
            }
        }
    }

    /// <returns>Verdadero si el evento quedó resuelto: aplicado, duplicado o en mensajes muertos.</returns>
    private async Task<bool> ProcesarAsync(ConsumeResult<string, string> registro, CancellationToken ct)
    {
        if (!Leer(registro, out var eventoId, out var evento))
            return await MensajeMuertoAsync(registro, $"El mensaje no cumple el contrato de {TipoEvento}.", ct);

        for (var intento = 0; ; intento++)
        {
            try
            {
                using var ambito = _ambitos.CreateScope();
                var aplicado = await AplicarAsync(ambito.ServiceProvider, registro.Topic, eventoId, evento!, ct);
                _log.LogInformation("Evento {Id} de {Tema}: {Resultado}", eventoId, registro.Topic, aplicado ? "aplicado" : "duplicado");
                return true;
            }
            catch (Exception e) when (e is not OperationCanceledException && FallosTransitorios.EsDeLaBase(e))
            {
                _log.LogWarning("La base no responde; el evento {Id} espera sin pasar a mensajes muertos: {Mensaje}",
                    eventoId, e.Message);
                await EsperarAsync(_opciones.EsperaDependencia, ct);
                return false;
            }
            catch (Exception e) when (e is not OperationCanceledException && intento < Esperas.Length)
            {
                _log.LogWarning("Falló la aplicación del evento {Id} (intento {N}): {Mensaje}", eventoId, intento + 1, e.Message);
                await EsperarAsync(Esperas[intento], ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return await MensajeMuertoAsync(registro, e.Message, ct);
            }
        }
    }

    private bool Leer(ConsumeResult<string, string> r, out Guid eventoId, out TEvento? evento)
    {
        evento = null;
        eventoId = Guid.Empty;
        var cabecera = r.Message.Headers?.FirstOrDefault(h => h.Key == "evento-id");
        if (cabecera is null || !Guid.TryParse(Encoding.UTF8.GetString(cabecera.GetValueBytes()), out eventoId))
            return false;
        try
        {
            evento = Interpretar(r.Message.Value);
            return evento is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <returns>Verdadero si el mensaje quedó en el tema de mensajes muertos; si el broker no lo aceptó, falso.</returns>
    private async Task<bool> MensajeMuertoAsync(ConsumeResult<string, string> r, string motivo, CancellationToken ct)
    {
        var cabeceras = new Headers();
        foreach (var h in r.Message.Headers ?? new Headers()) cabeceras.Add(h.Key, h.GetValueBytes());
        cabeceras.Add("dlt-motivo", Encoding.UTF8.GetBytes(motivo.Length > 200 ? motivo[..200] : motivo));
        cabeceras.Add("dlt-origen", Encoding.UTF8.GetBytes($"{r.Topic}/{r.Partition.Value}/{r.Offset.Value}"));
        try
        {
            await _kafka.Productor.ProduceAsync(r.Topic + TemasInstitucional.SufijoMensajesMuertos,
                new Message<string, string> { Key = r.Message.Key, Value = r.Message.Value, Headers = cabeceras }, ct);
        }
        catch (KafkaException e)
        {
            // Nunca se descarta en silencio: sin mensajes muertos, el evento se vuelve a leer después.
            _log.LogError("No fue posible escribir en mensajes muertos el evento de {Origen}; se reintentará: {Error}",
                r.TopicPartitionOffset, e.Error.Reason);
            await EsperarAsync(_opciones.EsperaDependencia, ct);
            return false;
        }
        _log.LogError("Evento enviado a mensajes muertos desde {Origen}: {Motivo}", r.TopicPartitionOffset, motivo);
        return true;
    }

    private void GuardarPosicion(IConsumer<string, string> consumidor, ConsumeResult<string, string> registro)
    {
        try
        {
            consumidor.StoreOffset(registro);
        }
        catch (KafkaException e)
        {
            // La partición se reasignó mientras se procesaba: su nuevo dueño lo relee desde
            // la última posición confirmada y evento_procesado descarta el duplicado.
            _log.LogWarning("No fue posible guardar la posición de {Origen}: {Error}", registro.TopicPartitionOffset, e.Error.Reason);
        }
    }

    private void VolverALeer(IConsumer<string, string> consumidor, ConsumeResult<string, string> registro)
    {
        try
        {
            consumidor.Seek(registro.TopicPartitionOffset);
        }
        catch (KafkaException e)
        {
            _log.LogWarning("No fue posible volver a {Origen}: {Error}", registro.TopicPartitionOffset, e.Error.Reason);
        }
    }

    /// <summary>Espera interrumpible: al apagar el servicio termina enseguida, y el ciclo ve la cancelación.</summary>
    private static async Task EsperarAsync(TimeSpan espera, CancellationToken ct)
    {
        try
        {
            await Task.Delay(espera, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>EV-09 indicators.updated: la fotografía de M7 de una institución.</summary>
public sealed class ConsumidorIndicadores(OpcionesKafka opciones, ProductorKafka kafka, IServiceScopeFactory ambitos,
    ILogger<ConsumidorIndicadores> log) : ConsumidorKafka<FotografiaIndicadores>(opciones, kafka, ambitos, log)
{
    public const string Tema = "redvital.donacion.indicators-updated.v1";
    private static readonly string[] Grupos = ["O-", "O+", "A-", "A+", "B-", "B+", "AB-", "AB+"];

    protected override IReadOnlyList<string> Temas => [Tema];
    protected override string TipoEvento => "indicators.updated";


    private sealed record Contrato(Guid InstitucionId, DateTimeOffset GeneradoEn, List<DiaContrato>? Captacion, List<ExistenciaContrato>? Inventario);
    private sealed record DiaContrato(DateOnly Dia, int Donaciones, int UnidadesGeneradas, int UnidadesNoAptas, int UnidadesVencidas);
    private sealed record ExistenciaContrato(string Componente, string GrupoSanguineo, int UnidadesDisponibles, int UnidadesPorVencer, bool BajoUmbral);

    protected override FotografiaIndicadores? Interpretar(string carga)
    {
        var c = JsonSerializer.Deserialize<Contrato>(carga, Json.Contrato);
        if (c is null || c.InstitucionId == Guid.Empty || c.GeneradoEn == default || c.Captacion is null || c.Inventario is null)
            return null;
        if (c.Captacion.Any(d => d.Donaciones < 0 || d.UnidadesGeneradas < 0 || d.UnidadesNoAptas < 0 || d.UnidadesVencidas < 0)
            || c.Captacion.Select(d => d.Dia).Distinct().Count() != c.Captacion.Count)
            return null;
        if (c.Inventario.Any(e => e.Componente is not { Length: > 0 and <= 30 } || !Grupos.Contains(e.GrupoSanguineo)
                                  || e.UnidadesDisponibles < 0 || e.UnidadesPorVencer < 0)
            || c.Inventario.Select(e => (e.Componente, e.GrupoSanguineo)).Distinct().Count() != c.Inventario.Count)
            return null;
        return new FotografiaIndicadores(c.InstitucionId, c.GeneradoEn,
            c.Captacion.Select(d => new DiaCaptacion(d.Dia, d.Donaciones, d.UnidadesGeneradas, d.UnidadesNoAptas, d.UnidadesVencidas)).ToList(),
            c.Inventario.Select(e => new ExistenciaRecibida(e.Componente, e.GrupoSanguineo, e.UnidadesDisponibles, e.UnidadesPorVencer, e.BajoUmbral)).ToList());
    }

    protected override Task<bool> AplicarAsync(IServiceProvider servicios, string tema, Guid eventoId, FotografiaIndicadores evento,
        CancellationToken ct) =>
        servicios.GetRequiredService<AplicarIndicadoresActualizados>().EjecutarAsync(eventoId, evento, ct);
}

/// <summary>EV-10 audit.recorded: un registro de la serie de Identidad, Campañas o Donación.</summary>
public sealed class ConsumidorAuditoria(OpcionesKafka opciones, ProductorKafka kafka, IServiceScopeFactory ambitos,
    ILogger<ConsumidorAuditoria> log) : ConsumidorKafka<RegistroAuditoria>(opciones, kafka, ambitos, log)
{
    /// <summary>El contexto de cada serie, según el tema del que llega.</summary>
    public static readonly IReadOnlyDictionary<string, string> ContextoPorTema = new Dictionary<string, string>
    {
        ["redvital.identidad.audit-recorded.v1"] = "identidad",
        ["redvital.campanias.audit-recorded.v1"] = "campanias",
        ["redvital.donacion.audit-recorded.v1"] = "donacion",
    };

    protected override IReadOnlyList<string> Temas => [.. ContextoPorTema.Keys];
    protected override string TipoEvento => "audit.recorded";

    protected override RegistroAuditoria? Interpretar(string carga) =>
        JsonSerializer.Deserialize<RegistroAuditoria>(carga, Json.Contrato) is { } r
        && r.Id != Guid.Empty && r.ActorTipo is "usuario" or "sistema" or "anonimo" && r.Resultado is "permitido" or "denegado"
        && !string.IsNullOrWhiteSpace(r.Operacion) && !string.IsNullOrWhiteSpace(r.RecursoTipo)
        && !string.IsNullOrWhiteSpace(r.CorrelacionId) && r.OcurridoEn != default
            ? r
            : null;

    protected override Task<bool> AplicarAsync(IServiceProvider servicios, string tema, Guid eventoId, RegistroAuditoria evento,
        CancellationToken ct) =>
        servicios.GetRequiredService<AplicarAuditoriaRegistrada>().EjecutarAsync(eventoId, ContextoPorTema[tema], evento, ct);
}

public static class Json
{
    /// <summary>snake_case y fechas ISO-8601, igual que el resto de contratos.</summary>
    public static readonly JsonSerializerOptions Contrato = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}
