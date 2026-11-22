// RedVital — institutional-service
// Lo que llega por Kafka de los demás servicios. Este servicio no le pregunta
// nada a nadie: indicadores y bitácora se componen con estas copias locales.
//   EV-09 indicators.updated (Donación): la fotografía de M7 de una institución.
//   EV-10 audit.recorded (Identidad, Campañas, Donación): un registro de su serie.
// Los dos son idempotentes: un evento repetido no tiene efecto.

using RedVital.Institucional.Aplicacion.Puertos;
using RedVital.Institucional.Dominio;

namespace RedVital.Institucional.Aplicacion.CasosDeUso;

public sealed class AplicarIndicadoresActualizados(IProyeccionIndicadores proyeccion)
{
    /// <returns>Falso si el evento ya se había aplicado.</returns>
    public Task<bool> EjecutarAsync(Guid eventoId, FotografiaIndicadores fotografia, CancellationToken ct) =>
        proyeccion.ReemplazarAsync(eventoId, fotografia, ct);
}

public sealed class AplicarAuditoriaRegistrada(IProyeccionBitacora proyeccion)
{
    /// <summary>Los contextos cuyas series compone la bitácora, además de la propia.</summary>
    public static readonly IReadOnlyList<string> Contextos = ["identidad", "campanias", "donacion"];

    /// <returns>Falso si el evento ya se había aplicado.</returns>
    public Task<bool> EjecutarAsync(Guid eventoId, string contexto, RegistroAuditoria registro, CancellationToken ct)
    {
        if (!Contextos.Contains(contexto)) throw new ArgumentException($"La bitácora no compone la serie de {contexto}.", nameof(contexto));
        return proyeccion.AnexarAsync(eventoId, contexto, registro, ct);
    }
}
