// RedVital — institutional-service
// Estructura común de las cuatro series de la bitácora (DD Sección 5,
// registro_auditoria). Sin campo para el contenido del recurso.

namespace RedVital.Institucional.Dominio;

public sealed record RegistroAuditoria(
    Guid Id,
    string ActorTipo,
    string? ActorId,
    string? Rol,
    string? JurisdiccionSolicitada,
    string Operacion,
    string RecursoTipo,
    Guid? RecursoId,
    string Resultado,
    string CorrelacionId,
    string? Origen,
    DateTimeOffset OcurridoEn)
{
    public static RegistroAuditoria De(Solicitante s, string operacion, string recursoTipo, Guid? recursoId,
        bool permitido, string correlacionId, string? origen, DateTimeOffset ahora, string? jurisdiccionSolicitada = null) => new(
        Guid.NewGuid(),
        s.EsAnonimo ? "anonimo" : s.Rol == Roles.Servicio ? "sistema" : "usuario",
        s.EsAnonimo ? null : s.Sub,
        s.Rol,
        jurisdiccionSolicitada,
        operacion,
        recursoTipo,
        recursoId,
        permitido ? "permitido" : "denegado",
        correlacionId,
        origen,
        ahora);
}
