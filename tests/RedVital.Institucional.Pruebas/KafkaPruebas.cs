// La mensajería de Institucional contra Kafka y PostgreSQL reales: el EV-07
// sale de la bandeja, el EV-09 y los EV-10 llegan a sus copias locales, y un
// mensaje que no cumple el contrato va a mensajes muertos sin detener al resto.

using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RedVital.Institucional.Api.Mensajeria;
using RedVital.Institucional.Aplicacion.CasosDeUso;
using RedVital.Institucional.Aplicacion.Puertos;
using RedVital.Institucional.Dominio;
using RedVital.Institucional.Infraestructura.Persistencia;
using Testcontainers.Kafka;
using Xunit;

namespace RedVital.Institucional.Pruebas;

/// <summary>Broker como el real: un tema que no está en el catálogo no existe.</summary>
public sealed class BrokerKafka : IAsyncLifetime
{
    private readonly KafkaContainer _kafka = new KafkaBuilder("apache/kafka:4.3.1")
        .WithEnvironment("KAFKA_AUTO_CREATE_TOPICS_ENABLE", "false")
        .Build();

    public string Servidores => _kafka.GetBootstrapAddress();

    public async Task InitializeAsync()
    {
        await _kafka.StartAsync();
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = Servidores }).Build();
        var temas = new[] { TemasInstitucional.InstitucionCambiada, ConsumidorIndicadores.Tema }
            .Concat(ConsumidorAuditoria.ContextoPorTema.Keys).ToList();
        await admin.CreateTopicsAsync(temas
            .Select(t => new TopicSpecification { Name = t, NumPartitions = 3, ReplicationFactor = 1 })
            .Concat(temas.Skip(1).Select(t => new TopicSpecification
                { Name = t + TemasInstitucional.SufijoMensajesMuertos, NumPartitions = 1, ReplicationFactor = 1 })));
    }

    public Task DisposeAsync() => _kafka.DisposeAsync().AsTask();
}

[Collection(ColeccionBase.Nombre)]
public class KafkaPruebas : IClassFixture<BrokerKafka>, IAsyncLifetime
{
    private readonly BaseInstitucional db;
    private readonly BrokerKafka _broker;
    private ServiceProvider _proveedor = null!;
    private readonly List<IHostedService> _servicios = [];

    public KafkaPruebas(BaseInstitucional db, BrokerKafka broker)
    {
        this.db = db;
        _broker = broker;
    }

    public async Task InitializeAsync()
    {
        var s = new ServiceCollection();
        s.AddLogging(l => l.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
        s.AddDbContext<InstitucionalDbContext>(o => o.UseNpgsql(db.CadenaServicio));
        s.AddScoped<IndicadoresProyectados>();
        s.AddScoped<IProyeccionIndicadores>(sp => sp.GetRequiredService<IndicadoresProyectados>());
        s.AddScoped<IProyeccionBitacora, BitacoraRecibida>();
        s.AddScoped<AplicarIndicadoresActualizados>();
        s.AddScoped<AplicarAuditoriaRegistrada>();
        s.AddSingleton(new OpcionesKafka
        {
            Habilitado = true, Servidores = _broker.Servidores, Protocolo = "Plaintext", EsperaDependencia = TimeSpan.FromMilliseconds(500),
        });
        s.AddSingleton<ProductorKafka>();
        s.AddSingleton<PublicadorBandeja>();
        s.AddSingleton<ConsumidorIndicadores>();
        s.AddSingleton<ConsumidorAuditoria>();
        _proveedor = s.BuildServiceProvider();
        foreach (var tipo in new[] { typeof(PublicadorBandeja), typeof(ConsumidorIndicadores), typeof(ConsumidorAuditoria) })
        {
            var servicio = (IHostedService)_proveedor.GetRequiredService(tipo);
            await servicio.StartAsync(CancellationToken.None);
            _servicios.Add(servicio);
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _servicios) await s.StopAsync(CancellationToken.None);
        await _proveedor.DisposeAsync();
    }

    [Fact]
    public async Task El_alta_de_una_institucion_sale_por_kafka()
    {
        Guid id;
        await using (var c = db.Contexto())
        {
            var bello = (await new RepositorioTerritorios(c).PorCodigoAsync("05088", CancellationToken.None))!;
            var i = Institucion.Crear($"KF-{Random.Shared.Next(100000, 999999)}", "Banco Kafka (ficticio)", TipoInstitucion.BancoDeSangre,
                bello, null, Guid.NewGuid(), DateTimeOffset.UtcNow);
            id = i.Id;
            new BandejaSalida(c).Encolar(new InstitucionCambiada(i.Id, i.Codigo, i.Nombre, "banco_de_sangre", "05088", "/00/05/05088",
                true, DateTimeOffset.UtcNow), "corr-kafka");
            await new RepositorioInstituciones(c).AgregarAsync(i, CancellationToken.None);
        }

        var mensaje = await LeerAsync(TemasInstitucional.InstitucionCambiada, m => m.Key == id.ToString());
        using (var carga = System.Text.Json.JsonDocument.Parse(mensaje.Value))
            Assert.Equal("/00/05/05088", carga.RootElement.GetProperty("territorio_ruta").GetString());
        Assert.Equal("institucional", Cabecera(mensaje, "productor"));
        await EsperarAsync(async () =>
        {
            await using var c = db.Contexto();
            return await c.EventosSalida.AnyAsync(e => e.Clave == id.ToString() && e.Estado == "publicado");
        });
    }

    [Fact]
    public async Task Los_indicadores_y_la_auditoria_llegan_a_sus_copias_y_lo_mal_formado_va_a_mensajes_muertos()
    {
        Guid institucion;
        await using (var c = db.Contexto())
        {
            var medellin = (await new RepositorioTerritorios(c).PorCodigoAsync("05001", CancellationToken.None))!;
            var i = Institucion.Crear($"KF-{Random.Shared.Next(100000, 999999)}", "Banco Indicadores Kafka (ficticio)",
                TipoInstitucion.BancoDeSangre, medellin, null, Guid.NewGuid(), DateTimeOffset.UtcNow);
            institucion = i.Id;
            await new RepositorioInstituciones(c).AgregarAsync(i, CancellationToken.None);
        }
        var correlacion = Guid.NewGuid().ToString();
        var malo = Guid.NewGuid();

        await PublicarAsync(ConsumidorIndicadores.Tema, institucion.ToString(), Guid.NewGuid(), $$"""
            {"institucion_id":"{{institucion}}","generado_en":"{{DateTimeOffset.UtcNow:O}}","desde":"2026-09-01","hasta":"2026-10-31",
             "captacion":[{"dia":"2026-10-02","donaciones":7,"unidades_generadas":21,"unidades_no_aptas":1,"unidades_vencidas":0}],
             "inventario":[{"componente":"plaquetas","grupo_sanguineo":"B+","unidades_disponibles":4,"unidades_por_vencer":4,"bajo_umbral":true}]}
            """);
        await PublicarAsync("redvital.campanias.audit-recorded.v1", correlacion, malo, """{"id":"no-es-un-uuid"}""");
        await PublicarAsync("redvital.campanias.audit-recorded.v1", correlacion, Guid.NewGuid(), $$"""
            {"id":"{{Guid.NewGuid()}}","actor_tipo":"usuario","actor_id":"u4","rol":"admin_banco","jurisdiccion_solicitada":null,
             "operacion":"publicar_campania","recurso_tipo":"campania","recurso_id":"{{Guid.NewGuid()}}","resultado":"permitido",
             "correlacion_id":"{{correlacion}}","origen":"10.0.0.4","ocurrido_en":"{{DateTimeOffset.UtcNow:O}}"}
            """);

        await EsperarAsync(async () =>
        {
            await using var c = db.Contexto();
            var existencias = await new IndicadoresProyectados(c).InventarioAsync(
                new FiltroIndicadores(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), "mes", institucion, RutaDe("/00/05/05001")),
                CancellationToken.None);
            var bitacora = await new FuenteAuditoriaRecibida("campanias", c).ConsultarAsync(
                new FiltroBitacora(null, null, correlacion, null, null, null, 10, null, null), CancellationToken.None);
            return existencias.Any(e => e.Componente == "plaquetas" && e.BajoUmbral) && bitacora.Any(r => r.Operacion == "publicar_campania");
        });
        var muerto = await LeerAsync("redvital.campanias.audit-recorded.v1" + TemasInstitucional.SufijoMensajesMuertos,
            m => Cabecera(m, "evento-id") == malo.ToString());
        Assert.Contains("audit.recorded", Cabecera(muerto, "dlt-motivo"));
    }

    // ------------------------------------------------------------------ utilidades

    private static RutaTerritorial RutaDe(string r)
    {
        Assert.True(RutaTerritorial.TryParse(r, out var ruta));
        return ruta;
    }

    private static string? Cabecera(Message<string, string> m, string nombre) =>
        m.Headers.FirstOrDefault(h => h.Key == nombre) is { } h ? Encoding.UTF8.GetString(h.GetValueBytes()) : null;

    private async Task PublicarAsync(string tema, string clave, Guid eventoId, string carga)
    {
        using var productor = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = _broker.Servidores }).Build();
        await productor.ProduceAsync(tema, new Message<string, string>
        {
            Key = clave,
            Value = carga,
            Headers = new Headers { { "evento-id", Encoding.UTF8.GetBytes(eventoId.ToString()) } },
        });
    }

    /// <summary>Lee el tema desde el principio hasta encontrar el mensaje, durante 30 s.</summary>
    private async Task<Message<string, string>> LeerAsync(string tema, Func<Message<string, string>, bool> buscado)
    {
        using var lector = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _broker.Servidores, GroupId = "prueba-" + Guid.NewGuid(),
            AutoOffsetReset = AutoOffsetReset.Earliest, EnableAutoCommit = false,
        }).Build();
        lector.Subscribe(tema);
        var limite = DateTime.UtcNow.AddSeconds(30);
        try
        {
            while (DateTime.UtcNow < limite)
            {
                ConsumeResult<string, string>? r;
                try
                {
                    r = lector.Consume(TimeSpan.FromMilliseconds(500));
                }
                catch (ConsumeException)
                {
                    await Task.Delay(200);
                    continue;
                }
                if (r is not null && buscado(r.Message)) return r.Message;
            }
        }
        finally
        {
            lector.Close();
        }
        Assert.Fail($"No llegó el mensaje esperado a {tema} en 30 segundos");
        return null!;
    }

    private static async Task EsperarAsync(Func<Task<bool>> condicion)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limite)
        {
            if (await condicion()) return;
            await Task.Delay(250);
        }
        Assert.Fail("La condición no se cumplió en 30 segundos");
    }
}
