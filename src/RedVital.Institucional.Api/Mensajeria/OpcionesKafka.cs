// RedVital — institutional-service. Conexión al broker (Confluent.Kafka sobre
// librdkafka). Un usuario SCRAM propio, con sus ACL: escribe
// institution-changed y lee indicators-updated y los tres audit-recorded.

using Confluent.Kafka;

namespace RedVital.Institucional.Api.Mensajeria;

public sealed class OpcionesKafka
{
    public bool Habilitado { get; set; }
    public string Servidores { get; set; } = "kafka:9092";
    public string Usuario { get; set; } = "institucional";
    public string? Clave { get; set; }
    public string Protocolo { get; set; } = "SaslPlaintext";

    /// <summary>Cada cuánto el consumidor vuelve a intentar un evento mientras la base o el broker no responden.</summary>
    public TimeSpan EsperaDependencia { get; set; } = TimeSpan.FromSeconds(5);

    public T Aplicar<T>(T config) where T : ClientConfig
    {
        config.BootstrapServers = Servidores;
        config.SecurityProtocol = Enum.Parse<SecurityProtocol>(Protocolo, ignoreCase: true);
        if (config.SecurityProtocol is SecurityProtocol.SaslPlaintext or SecurityProtocol.SaslSsl)
        {
            config.SaslMechanism = SaslMechanism.ScramSha512;
            config.SaslUsername = Usuario;
            config.SaslPassword = Clave;
        }
        return config;
    }
}

/// <summary>
/// Productor único del servicio: idempotente y con confirmación de todas las
/// réplicas. Lo usan el publicador de la bandeja y el envío a mensajes muertos.
/// </summary>
public sealed class ProductorKafka : IDisposable
{
    public IProducer<string, string> Productor { get; }

    public ProductorKafka(OpcionesKafka opciones) =>
        Productor = new ProducerBuilder<string, string>(opciones.Aplicar(new ProducerConfig
        {
            EnableIdempotence = true,
            Acks = Acks.All,
            MessageTimeoutMs = 10_000,
            ClientId = "institucional",
        })).Build();

    public void Dispose()
    {
        Productor.Flush(TimeSpan.FromSeconds(5));
        Productor.Dispose();
    }
}
