// RedVital — institutional-service
// M6, jerarquía territorial (RF-09, DD Tabla 32).
//   GET  /v1/territorios  U4 a U7, acotado a la jurisdicción
//   POST /v1/territorios  solo U6; la ruta la calcula el servicio

using RedVital.Institucional.Aplicacion.Puertos;
using RedVital.Institucional.Dominio;

namespace RedVital.Institucional.Aplicacion.CasosDeUso;

public sealed record TerritorioRespuesta(Guid Id, string CodigoDivipola, string Nombre, string Nivel, Guid? PadreId,
    string Ruta, bool Vigente)
{
    public static TerritorioRespuesta Desde(Territorio t) =>
        new(t.Id, t.CodigoDivipola, t.Nombre, Niveles.Codigo(t.Nivel), t.PadreId, t.Ruta, t.Vigente);
}

public sealed record ConsultaTerritorios(string? Nivel, Guid? PadreId, int? Pagina, int? Tamano);

public sealed class ConsultarTerritorios(IRepositorioTerritorios territorios, ResolutorJurisdiccion jurisdiccion, Denegaciones denegaciones)
{
    public async Task<Resultado<Paginado<TerritorioRespuesta>>> EjecutarAsync(ContextoPeticion ctx, ConsultaTerritorios c, CancellationToken ct)
    {
        if (!PoliticaAcceso.LeeTerritorios(ctx.Solicitante))
            return await denegaciones.RegistrarAsync<Paginado<TerritorioRespuesta>>(ctx, "consultar_territorios", "territorio", null, Fallo.NoPermitido, ct: ct);
        var nivel = Niveles.Desde(c.Nivel);
        if (c.Nivel is not null && nivel is null) return Resultado<Paginado<TerritorioRespuesta>>.Error(Fallo.Invalido, "nivel es nacional, departamental o municipal.");
        if (!Paginacion.Validar(c.Pagina, c.Tamano, 200, out var pagina, out var tamano))
            return Resultado<Paginado<TerritorioRespuesta>>.Error(Fallo.Invalido, Paginacion.Mensaje);
        if (await jurisdiccion.AmbitoAsync(ctx.Solicitante, ct) is not { } ambito)
            return await denegaciones.RegistrarAsync<Paginado<TerritorioRespuesta>>(ctx, "consultar_territorios", "territorio", null, Fallo.NoPermitido, ct: ct);

        var r = await territorios.ListarAsync(new FiltroTerritorios(ambito.Ruta, nivel, c.PadreId, pagina, tamano), ct);
        return Resultado<Paginado<TerritorioRespuesta>>.Ok(r.Map(TerritorioRespuesta.Desde));
    }
}

public sealed record NuevoTerritorio(string? CodigoDivipola, string? Nombre);

public sealed class CrearTerritorio(IRepositorioTerritorios territorios, Denegaciones denegaciones)
{
    public async Task<Resultado<TerritorioRespuesta>> EjecutarAsync(ContextoPeticion ctx, NuevoTerritorio n, CancellationToken ct)
    {
        if (!PoliticaAcceso.CreaTerritorios(ctx.Solicitante))
            return await denegaciones.RegistrarAsync<TerritorioRespuesta>(ctx, "crear_territorio", "territorio", null, Fallo.NoPermitido, ct: ct);
        if (string.IsNullOrWhiteSpace(n.CodigoDivipola))
            return Resultado<TerritorioRespuesta>.Error(Fallo.Invalido, "codigo_divipola es obligatorio.");

        try
        {
            var codigo = n.CodigoDivipola.Trim();
            var ruta = RutaTerritorial.DeCodigo(codigo);
            // El padre se deduce del código: la nación para un departamento, el
            // departamento de los dos primeros dígitos para un municipio.
            var codigoPadre = ruta.Nivel == NivelTerritorial.Municipal ? codigo[..2] : "00";
            if (await territorios.PorCodigoAsync(codigoPadre, ct) is not { } padre)
                return Resultado<TerritorioRespuesta>.Error(Fallo.Invalido, $"No existe el territorio {codigoPadre}, del que dependería.");
            var nuevo = Territorio.Crear(codigo, n.Nombre ?? "", padre);
            await territorios.AgregarAsync(nuevo, ct);
            await denegaciones.PermitidoAsync(ctx, "crear_territorio", "territorio", nuevo.Id, ct);
            return Resultado<TerritorioRespuesta>.Ok(TerritorioRespuesta.Desde(nuevo));
        }
        catch (ReglaDominio e)
        {
            return Resultado<TerritorioRespuesta>.Error(Fallo.Invalido, e.Message);
        }
        catch (Duplicado)
        {
            return Resultado<TerritorioRespuesta>.Error(Fallo.Conflicto, "Ya existe un territorio con ese código DIVIPOLA.");
        }
    }
}
