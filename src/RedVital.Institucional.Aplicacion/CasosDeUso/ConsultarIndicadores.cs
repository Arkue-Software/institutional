// RedVital — institutional-service
// M7, tablero de indicadores (RF-13, DD Tabla 32). Solo lectura (RI-03): la
// captación y el inventario salen de la copia local de los agregados que
// Donación publica por Kafka (EV-09); la cobertura territorial es propia. La
// respuesta dice de cuándo es la fotografía más antigua que usó, para que
// nadie tome por actual una cifra que todavía no llega.

using RedVital.Institucional.Aplicacion.Puertos;
using RedVital.Institucional.Dominio;

namespace RedVital.Institucional.Aplicacion.CasosDeUso;

public sealed record ConsultaIndicadores(DateOnly? Desde, DateOnly? Hasta, string? AgruparPor, Guid? InstitucionId, string? TerritorioRuta);

public sealed record FiltroAplicado(DateOnly Desde, DateOnly Hasta, string AgruparPor, Guid? InstitucionId, string? TerritorioRuta);

public sealed record IndicadoresRespuesta(
    FiltroAplicado Filtro,
    DateTimeOffset? ActualizadoEn,
    IReadOnlyList<PeriodoCaptacion> Captacion,
    IReadOnlyList<ExistenciaAgregada> Inventario,
    IReadOnlyList<CoberturaDepartamento> Cobertura);

public sealed class ConsultarIndicadores(IConsultaIndicadores indicadores, IConsultaCobertura cobertura,
    IRepositorioInstituciones instituciones, ResolutorJurisdiccion jurisdiccion, Denegaciones denegaciones, TimeProvider reloj)
{
    private const string Operacion = "consultar_indicadores";
    private static readonly string[] Agrupaciones = ["dia", "semana", "mes"];
    public const int DiasMaximos = 731;

    public async Task<Resultado<IndicadoresRespuesta>> EjecutarAsync(ContextoPeticion ctx, ConsultaIndicadores c, CancellationToken ct)
    {
        var s = ctx.Solicitante;
        if (!PoliticaAcceso.LeeIndicadores(s))
            return await denegaciones.RegistrarAsync<IndicadoresRespuesta>(ctx, Operacion, "indicador", null, Fallo.NoPermitido, ct: ct);

        var hoy = DateOnly.FromDateTime(reloj.GetUtcNow().UtcDateTime);
        var hasta = c.Hasta ?? hoy;
        var desde = c.Desde ?? hasta.AddDays(-89);
        var agrupar = c.AgruparPor ?? "mes";
        if (!Agrupaciones.Contains(agrupar)) return Invalido("agrupar_por es dia, semana o mes.");
        if (desde > hasta) return Invalido("desde no puede ser posterior a hasta.");
        if (hasta.DayNumber - desde.DayNumber >= DiasMaximos) return Invalido("El periodo admite hasta dos años.");
        if (c.InstitucionId is not null && c.TerritorioRuta is not null) return Invalido("Indique institucion_id o territorio_ruta, no ambos.");

        if (await jurisdiccion.AmbitoAsync(s, ct) is not { } ambito)
            return await denegaciones.RegistrarAsync<IndicadoresRespuesta>(ctx, Operacion, "indicador", null, Fallo.NoPermitido, ct: ct);

        // Qué se mide: la institución o la ruta pedidas, si caen en la
        // jurisdicción; si no se pide nada, la jurisdicción entera.
        Guid? institucion = null;
        RutaTerritorial alcance;
        if (c.InstitucionId is { } pedida)
        {
            if (await instituciones.VistaAsync(pedida, ct) is not { } v || !RutaTerritorial.TryParse(v.TerritorioRuta, out var rutaInst))
                return Resultado<IndicadoresRespuesta>.Error(Fallo.NoEncontrado);
            if (ambito.Institucion is { } propia ? propia != pedida : !ambito.Ruta.Contiene(rutaInst))
                return await Fuera(ctx, $"institucion:{pedida}", ct);
            institucion = pedida;
            alcance = rutaInst;
        }
        else if (c.TerritorioRuta is not null)
        {
            if (!RutaTerritorial.TryParse(c.TerritorioRuta, out var ruta)) return Invalido("territorio_ruta no es una ruta territorial válida.");
            if (ambito.Institucion is not null || !ambito.Ruta.Contiene(ruta)) return await Fuera(ctx, $"territorio:{ruta}", ct);
            alcance = ruta;
        }
        else
        {
            institucion = ambito.Institucion;
            alcance = ambito.Ruta;
        }

        var filtro = new FiltroIndicadores(desde, hasta, agrupar, institucion, alcance);
        var captacion = await indicadores.CaptacionAsync(filtro, ct);
        var inventario = await indicadores.InventarioAsync(filtro, ct);
        var actualizado = await indicadores.ActualizadoEnAsync(filtro, ct);
        var territorial = await cobertura.DentroDeAsync(alcance, ct);
        return Resultado<IndicadoresRespuesta>.Ok(new IndicadoresRespuesta(
            new FiltroAplicado(desde, hasta, agrupar, institucion, institucion is null ? alcance.Valor : null),
            actualizado, captacion, inventario, territorial));
    }

    private Task<Resultado<IndicadoresRespuesta>> Fuera(ContextoPeticion ctx, string solicitada, CancellationToken ct) =>
        denegaciones.RegistrarAsync<IndicadoresRespuesta>(ctx, Operacion, "indicador", null, Fallo.FueraDeJurisdiccion, solicitada, ct);

    private static Resultado<IndicadoresRespuesta> Invalido(string detalle) => Resultado<IndicadoresRespuesta>.Error(Fallo.Invalido, detalle);
}
