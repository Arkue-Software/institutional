// RedVital — institutional-service
// M6, registro de instituciones (RF-09, EC-42, DD Tablas 32 y 13.2).
//   GET  /v1/instituciones        U4 a U7, acotado a la jurisdicción
//   GET  /v1/instituciones/{id}   todo perfil con cuenta (nombre, tipo y territorio no son restringidos)
//   POST /v1/instituciones        U5 dentro de su jurisdicción, U6
//   PUT  /v1/instituciones/{id}   U5 dentro de su jurisdicción, U6; el tipo no cambia
// Cada alta o cambio sale a Kafka (EV-07) en la misma transacción: Campañas y
// Donación guardan su propia copia y nunca le preguntan a este servicio.

using RedVital.Institucional.Aplicacion.Puertos;
using RedVital.Institucional.Dominio;

namespace RedVital.Institucional.Aplicacion.CasosDeUso;

public sealed record ConsultaInstituciones(Guid? TerritorioId, string? TerritorioRuta, bool? IncluirDescendientes,
    string? Tipo, bool? IncluirBajas, int? Pagina, int? Tamano);

public sealed class ConsultarInstituciones(IRepositorioInstituciones instituciones, IRepositorioTerritorios territorios,
    ResolutorJurisdiccion jurisdiccion, Denegaciones denegaciones)
{
    private const string Operacion = "consultar_instituciones";

    public async Task<Resultado<Paginado<VistaInstitucion>>> EjecutarAsync(ContextoPeticion ctx, ConsultaInstituciones c, CancellationToken ct)
    {
        var s = ctx.Solicitante;
        if (!PoliticaAcceso.LeeInstituciones(s))
            return await denegaciones.RegistrarAsync<Paginado<VistaInstitucion>>(ctx, Operacion, "institucion", null, Fallo.NoPermitido, ct: ct);
        if (!Paginacion.Validar(c.Pagina, c.Tamano, 50, out var pagina, out var tamano))
            return Error(Paginacion.Mensaje);
        var tipo = Tipos.Desde(c.Tipo);
        if (c.Tipo is not null && tipo is null) return Error("tipo es banco_de_sangre o servicio_transfusion.");

        // Territorio pedido: por id o por ruta, nunca los dos.
        RutaTerritorial? pedida = null;
        if (c.TerritorioId is not null && c.TerritorioRuta is not null) return Error("Indique territorio_id o territorio_ruta, no ambos.");
        if (c.TerritorioId is { } tid)
        {
            if (await territorios.PorIdAsync(tid, ct) is not { } t) return Error("territorio_id no existe.");
            pedida = t.RutaTerritorial;
        }
        else if (c.TerritorioRuta is not null)
        {
            if (!RutaTerritorial.TryParse(c.TerritorioRuta, out var r)) return Error("territorio_ruta no es una ruta territorial válida.");
            pedida = r;
        }

        var filtro = new FiltroInstituciones(pedida, c.IncluirDescendientes ?? true, tipo, null,
            c.IncluirBajas == true && PoliticaAcceso.EscribeInstituciones(s), pagina, tamano);

        if (await jurisdiccion.AmbitoAsync(s, ct) is not { } ambito)
            return await denegaciones.RegistrarAsync<Paginado<VistaInstitucion>>(ctx, Operacion, "institucion", null, Fallo.NoPermitido, ct: ct);
        if (pedida is not null && !ambito.Ruta.Contiene(pedida) && !(ambito.Institucion is not null && pedida.Contiene(ambito.Ruta)))
            return await denegaciones.RegistrarAsync<Paginado<VistaInstitucion>>(ctx, Operacion, "institucion", null,
                Fallo.FueraDeJurisdiccion, $"territorio:{pedida}", ct);

        // El administrador de banco ve la suya; los demás, las de su ruta.
        filtro = ambito.Institucion is { } propia
            ? filtro with { SoloId = propia, Ruta = null }
            : filtro with { Ruta = pedida ?? ambito.Ruta };
        return Resultado<Paginado<VistaInstitucion>>.Ok(await instituciones.ListarAsync(filtro, ct));
    }

    private static Resultado<Paginado<VistaInstitucion>> Error(string detalle) => Resultado<Paginado<VistaInstitucion>>.Error(Fallo.Invalido, detalle);
}

public sealed class ResolverInstitucion(IRepositorioInstituciones instituciones)
{
    /// <summary>
    /// Traduce un institucion_id a su nombre y territorio para la web (DD 13.2).
    /// Una institución dada de baja se sigue resolviendo, con vigente falso.
    /// </summary>
    public async Task<Resultado<VistaInstitucion>> EjecutarAsync(ContextoPeticion ctx, Guid id, CancellationToken ct)
    {
        if (!PoliticaAcceso.ResuelveInstitucion(ctx.Solicitante)) return Resultado<VistaInstitucion>.Error(Fallo.NoPermitido);
        return await instituciones.VistaAsync(id, ct) is { } v
            ? Resultado<VistaInstitucion>.Ok(v)
            : Resultado<VistaInstitucion>.Error(Fallo.NoEncontrado);
    }
}

public sealed record NuevaInstitucion(string? Codigo, string? Nombre, string? Tipo, Guid? TerritorioId, string? Direccion);

public sealed class CrearInstitucion(IRepositorioInstituciones instituciones, IRepositorioTerritorios territorios,
    ResolutorJurisdiccion jurisdiccion, Denegaciones denegaciones, IBandejaSalida bandeja, TimeProvider reloj)
{
    private const string Operacion = "crear_institucion";

    public async Task<Resultado<VistaInstitucion>> EjecutarAsync(ContextoPeticion ctx, NuevaInstitucion n, CancellationToken ct)
    {
        var s = ctx.Solicitante;
        if (!PoliticaAcceso.EscribeInstituciones(s) || s.CuentaId is not { } autor)
            return await denegaciones.RegistrarAsync<VistaInstitucion>(ctx, Operacion, "institucion", null, Fallo.NoPermitido, ct: ct);
        if (Tipos.Desde(n.Tipo) is not { } tipo)
            return Resultado<VistaInstitucion>.Error(Fallo.Invalido, "tipo es banco_de_sangre o servicio_transfusion.");
        if (n.TerritorioId is not { } tid || await territorios.PorIdAsync(tid, ct) is not { } municipio)
            return Resultado<VistaInstitucion>.Error(Fallo.Invalido, "territorio_id debe ser un municipio existente.");

        // El coordinador solo da de alta dentro de su jurisdicción.
        if (await jurisdiccion.AmbitoAsync(s, ct) is not { Institucion: null } ambito || !ambito.Ruta.Contiene(municipio.RutaTerritorial))
            return await denegaciones.RegistrarAsync<VistaInstitucion>(ctx, Operacion, "institucion", null,
                Fallo.FueraDeJurisdiccion, $"territorio:{municipio.Ruta}", ct);

        try
        {
            var ahora = reloj.GetUtcNow();
            var nueva = Institucion.Crear(n.Codigo ?? "", n.Nombre ?? "", tipo, municipio, n.Direccion, autor, ahora);
            bandeja.Encolar(Eventos.Cambio(nueva, municipio.CodigoDivipola, municipio.Ruta, ahora), ctx.CorrelacionId);
            await instituciones.AgregarAsync(nueva, ct);
            await denegaciones.PermitidoAsync(ctx, Operacion, "institucion", nueva.Id, ct);
            return Resultado<VistaInstitucion>.Ok((await instituciones.VistaAsync(nueva.Id, ct))!);
        }
        catch (ReglaDominio e)
        {
            return Resultado<VistaInstitucion>.Error(Fallo.Invalido, e.Message);
        }
        catch (Duplicado)
        {
            return Resultado<VistaInstitucion>.Error(Fallo.Conflicto, "Ya existe una institución con ese código de habilitación.");
        }
    }
}

public sealed record CambioInstitucion(string? Nombre, string? Direccion, bool? Vigente, string? Tipo);

public sealed class ModificarInstitucion(IRepositorioInstituciones instituciones, ResolutorJurisdiccion jurisdiccion,
    Denegaciones denegaciones, IBandejaSalida bandeja, TimeProvider reloj)
{
    public async Task<Resultado<VistaInstitucion>> EjecutarAsync(ContextoPeticion ctx, Guid id, CambioInstitucion c, CancellationToken ct)
    {
        var operacion = c.Vigente == false ? "baja_institucion" : "modificar_institucion";
        var s = ctx.Solicitante;
        if (!PoliticaAcceso.EscribeInstituciones(s))
            return await denegaciones.RegistrarAsync<VistaInstitucion>(ctx, operacion, "institucion", id, Fallo.NoPermitido, ct: ct);
        if (await instituciones.PorIdAsync(id, ct) is not { } institucion || await instituciones.VistaAsync(id, ct) is not { } vista)
            return Resultado<VistaInstitucion>.Error(Fallo.NoEncontrado);

        // Fuera de la jurisdicción responde como si no existiera, y queda auditado.
        RutaTerritorial.TryParse(vista.TerritorioRuta, out var ruta);
        if (await jurisdiccion.AmbitoAsync(s, ct) is not { Institucion: null } ambito || !ambito.Ruta.Contiene(ruta))
            return await denegaciones.RegistrarAsync<VistaInstitucion>(ctx, operacion, "institucion", id,
                Fallo.NoEncontrado, $"institucion:{id}", ct);

        if (c.Tipo is not null && Tipos.Desde(c.Tipo) != institucion.Tipo)
            return Resultado<VistaInstitucion>.Error(Fallo.Conflicto, "El tipo de una institución no cambia.");
        if (c.Nombre is null && c.Direccion is null && c.Vigente is null)
            return Resultado<VistaInstitucion>.Error(Fallo.Invalido, "Indique nombre, direccion o vigente.");
        try
        {
            institucion.Modificar(c.Nombre, c.Direccion, c.Vigente);
        }
        catch (ReglaDominio e)
        {
            return Resultado<VistaInstitucion>.Error(Fallo.Invalido, e.Message);
        }
        bandeja.Encolar(Eventos.Cambio(institucion, vista.TerritorioCodigo, vista.TerritorioRuta, reloj.GetUtcNow()), ctx.CorrelacionId);
        await instituciones.GuardarAsync(institucion, ct);
        await denegaciones.PermitidoAsync(ctx, operacion, "institucion", id, ct);
        return Resultado<VistaInstitucion>.Ok((await instituciones.VistaAsync(id, ct))!);
    }
}

internal static class Eventos
{
    public static InstitucionCambiada Cambio(Institucion i, string territorioCodigo, string territorioRuta, DateTimeOffset cuando) =>
        new(i.Id, i.Codigo, i.Nombre, Tipos.Codigo(i.Tipo), territorioCodigo, territorioRuta, i.Vigente, cuando);
}
