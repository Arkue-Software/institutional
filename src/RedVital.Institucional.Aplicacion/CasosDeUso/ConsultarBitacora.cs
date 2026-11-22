// RedVital — institutional-service
// ST2, consulta de la bitácora (RF-21, ADR-016, DD Tabla 32). Solo U7 y sin
// ninguna escritura (RI-04). Compone las cuatro series —la propia y las copias
// de Identidad, Campañas y Donación que llegan por Kafka (EV-10)—, ordenadas
// de más reciente a más antigua. La respuesta dice hasta cuándo está al día
// cada serie: un registro que todavía viaja por el broker no se da por
// inexistente. Cada consulta queda, a su vez, auditada en la serie propia.

using System.Text;
using RedVital.Institucional.Aplicacion.Puertos;
using RedVital.Institucional.Dominio;

namespace RedVital.Institucional.Aplicacion.CasosDeUso;

public sealed record ConsultaBitacora(string? RecursoTipo, Guid? RecursoId, string? CorrelacionId, string? ActorId,
    DateTimeOffset? Desde, DateTimeOffset? Hasta, int? Tamano, string? AntesDe);

public sealed record RegistroBitacora(string Contexto, RegistroAuditoria Registro);

public sealed record SerieAlDia(string Contexto, DateTimeOffset? AlDiaHasta);

public sealed record BitacoraRespuesta(IReadOnlyList<RegistroBitacora> Registros, string? Siguiente, IReadOnlyList<SerieAlDia> Series);

public sealed class ConsultarBitacora(IEnumerable<IFuenteAuditoria> fuentes, Denegaciones denegaciones)
{
    private const string Operacion = "consultar_bitacora";
    public const int TamanoMaximo = 200;

    public async Task<Resultado<BitacoraRespuesta>> EjecutarAsync(ContextoPeticion ctx, ConsultaBitacora c, CancellationToken ct)
    {
        if (!PoliticaAcceso.LeeBitacora(ctx.Solicitante))
            return await denegaciones.RegistrarAsync<BitacoraRespuesta>(ctx, Operacion, "bitacora", null, Fallo.NoPermitido, ct: ct);
        var tamano = c.Tamano ?? 50;
        if (tamano is < 1 or > TamanoMaximo) return Invalido($"tamano entre 1 y {TamanoMaximo}.");
        if (c.Desde > c.Hasta) return Invalido("desde no puede ser posterior a hasta.");
        if (c.CorrelacionId is { Length: > 36 } || c.ActorId is { Length: > 64 } || c.RecursoTipo is { Length: > 40 })
            return Invalido("Algún filtro excede su longitud máxima.");
        Cursor? cursor = null;
        if (c.AntesDe is not null && (cursor = Cursor.Leer(c.AntesDe)) is null) return Invalido("antes_de no es un cursor válido.");

        // Se pide un registro de más a cada serie: si sobra alguno, hay otra página.
        var filtro = new FiltroBitacora(c.RecursoTipo, c.RecursoId, c.CorrelacionId, c.ActorId, c.Desde, c.Hasta,
            tamano + 1, cursor?.OcurridoEn, cursor?.Id);
        var series = new List<SerieAlDia>();
        var fusion = new List<RegistroBitacora>();
        foreach (var f in fuentes)
        {
            fusion.AddRange((await f.ConsultarAsync(filtro, ct)).Select(x => new RegistroBitacora(f.Contexto, x)));
            series.Add(new SerieAlDia(f.Contexto, await f.AlDiaHastaAsync(ct)));
        }
        fusion = fusion
            .OrderByDescending(x => x.Registro.OcurridoEn)
            .ThenByDescending(x => x.Registro.Id.ToString(), StringComparer.Ordinal)
            .ToList();
        var pagina = fusion.Take(tamano).ToList();
        var siguiente = fusion.Count > tamano ? new Cursor(pagina[^1].Registro.OcurridoEn, pagina[^1].Registro.Id).Escribir() : null;

        await denegaciones.PermitidoAsync(ctx, Operacion, "bitacora", null, ct);
        return Resultado<BitacoraRespuesta>.Ok(new BitacoraRespuesta(pagina, siguiente,
            series.OrderBy(x => x.Contexto, StringComparer.Ordinal).ToList()));
    }

    private static Resultado<BitacoraRespuesta> Invalido(string detalle) => Resultado<BitacoraRespuesta>.Error(Fallo.Invalido, detalle);

    /// <summary>Posición opaca: el par (ocurrido_en, id) del último registro entregado.</summary>
    public sealed record Cursor(DateTimeOffset OcurridoEn, Guid Id)
    {
        public string Escribir() =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{OcurridoEn.UtcTicks}|{Id}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static Cursor? Leer(string texto)
        {
            try
            {
                var b64 = texto.Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                var partes = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|');
                return partes.Length == 2 && long.TryParse(partes[0], out var ticks) && Guid.TryParse(partes[1], out var id)
                    ? new Cursor(new DateTimeOffset(ticks, TimeSpan.Zero), id)
                    : null;
            }
            catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}
