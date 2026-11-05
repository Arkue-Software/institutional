// RedVital — institutional-service
// Resuelve la jurisdicción del solicitante como ruta territorial (EC-01).
// Un coordinador, el administrador nacional o el auditor la traen en el token;
// la de un administrador de banco es el municipio de su institución.

using RedVital.Institucional.Aplicacion.Puertos;
using RedVital.Institucional.Dominio;

namespace RedVital.Institucional.Aplicacion.CasosDeUso;

public sealed record Ambito(RutaTerritorial Ruta, Guid? Institucion);

public sealed class ResolutorJurisdiccion(IRepositorioInstituciones instituciones)
{
    /// <summary>Nulo si el token no trae una jurisdicción utilizable: se falla cerrado.</summary>
    public async Task<Ambito?> AmbitoAsync(Solicitante s, CancellationToken ct)
    {
        if (s.RutaJurisdiccion is { } ruta) return new Ambito(ruta, null);
        if (s.InstitucionJurisdiccion is { } id && await instituciones.VistaAsync(id, ct) is { } propia &&
            RutaTerritorial.TryParse(propia.TerritorioRuta, out var municipio))
            return new Ambito(municipio, id);
        return null;
    }
}

/// <summary>Registra las denegaciones F3 del contexto (Tabla 16) con un solo formato.</summary>
public sealed class Denegaciones(IRegistroAuditoria auditoria, TimeProvider reloj)
{
    public async Task<Resultado<T>> RegistrarAsync<T>(ContextoPeticion ctx, string operacion, string recursoTipo,
        Guid? recursoId, Fallo fallo, string? jurisdiccionSolicitada = null, CancellationToken ct = default)
    {
        await auditoria.RegistrarAsync(RegistroAuditoria.De(ctx.Solicitante, operacion, recursoTipo, recursoId, false,
            ctx.CorrelacionId, ctx.Origen, reloj.GetUtcNow(), jurisdiccionSolicitada), ct);
        return Resultado<T>.Error(fallo);
    }

    public Task PermitidoAsync(ContextoPeticion ctx, string operacion, string recursoTipo, Guid? recursoId, CancellationToken ct) =>
        auditoria.RegistrarAsync(RegistroAuditoria.De(ctx.Solicitante, operacion, recursoTipo, recursoId, true,
            ctx.CorrelacionId, ctx.Origen, reloj.GetUtcNow()), ct);
}
