// RedVital — institutional-service. Adaptadores de persistencia de lo que
// entra y sale por Kafka (ADR-018):
//   - la bandeja de salida de EV-07, que se guarda con el cambio de la institución;
//   - la copia de los agregados de M7 (EV-09) y su lectura para el tablero;
//   - la copia de las series de auditoría de los otros contextos (EV-10).
// Cada evento aplicado queda en evento_procesado en la misma transacción que
// su efecto: un duplicado no cambia nada.

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RedVital.Institucional.Aplicacion.Puertos;
using RedVital.Institucional.Dominio;

namespace RedVital.Institucional.Infraestructura.Persistencia;

public sealed class FilaEventoSalida
{
    public Guid Id { get; set; }
    public long Secuencia { get; set; }
    public required string Tipo { get; set; }
    public int VersionEsquema { get; set; }
    public required string Tema { get; set; }
    public required string Clave { get; set; }
    public required string Carga { get; set; }
    public string? ClaveNatural { get; set; }
    public required string CorrelacionId { get; set; }
    public string Estado { get; set; } = "pendiente";
    public int IntentosPublicacion { get; set; }
    public DateTimeOffset CreadoEn { get; set; }
    public DateTimeOffset? PublicadoEn { get; set; }
    public DateTimeOffset? ExpiraEn { get; set; }
}

public static class TemasInstitucional
{
    /// <summary>EV-07, productor Institucional; lo consumen Campañas y Donación.</summary>
    public const string InstitucionCambiada = "redvital.institucional.institution-changed.v1";
    public const string TipoInstitucionCambiada = "institution.changed";
    /// <summary>Grupo consumidor de Institucional (DD, sección 14).</summary>
    public const string Grupo = "institucional";
    public const string SufijoMensajesMuertos = ".institucional.dlt";
}

public sealed class BandejaSalida(InstitucionalDbContext contexto) : IBandejaSalida
{
    private static readonly JsonSerializerOptions Contrato = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public void Encolar(InstitucionCambiada e, string correlacionId) =>
        contexto.EventosSalida.Add(new FilaEventoSalida
        {
            Id = Guid.NewGuid(),
            Tipo = TemasInstitucional.TipoInstitucionCambiada,
            VersionEsquema = 1,
            Tema = TemasInstitucional.InstitucionCambiada,
            // Por institución: sus cambios llegan en orden.
            Clave = e.InstitucionId.ToString(),
            Carga = JsonSerializer.Serialize(e, Contrato),
            CorrelacionId = correlacionId.Length > 36 ? correlacionId[..36] : correlacionId,
            CreadoEn = e.CambiadoEn,
        });
}

internal static class Procesados
{
    private static readonly TimeSpan Vigencia = TimeSpan.FromDays(14);

    /// <summary>Falso si el evento ya estaba registrado: es un duplicado.</summary>
    public static async Task<bool> RegistrarAsync(InstitucionalDbContext contexto, Guid eventoId, string tipo, CancellationToken ct)
    {
        var ahora = DateTimeOffset.UtcNow;
        return await contexto.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO evento_procesado (grupo_consumidor, evento_id, tipo, procesado_en, expira_en)
            VALUES ({TemasInstitucional.Grupo}, {eventoId}, {tipo}, {ahora}, {ahora.Add(Vigencia)})
            ON CONFLICT (grupo_consumidor, evento_id) DO NOTHING
            """, ct) == 1;
    }

    /// <summary>Con tipo explícito: un nulo sin tipo no se compararía con uuid ni con timestamptz.</summary>
    public static NpgsqlParameter P(string nombre, NpgsqlDbType tipo, object? valor) => new(nombre, tipo) { Value = valor ?? DBNull.Value };
}

/// <summary>M7 sobre la copia local de EV-09: captación por periodo, existencias y frescura.</summary>
public sealed class IndicadoresProyectados(InstitucionalDbContext contexto) : IConsultaIndicadores, IProyeccionIndicadores
{
    /// <summary>Con menos donaciones que esto en un periodo, no se muestran las no aptas (supresión por agregación).</summary>
    public const int MinimoParaMostrarNoAptas = 5;

    // Las instituciones medidas: una, o todas las del territorio y sus descendientes.
    private const string Alcance = """
        WITH alcance AS (
            SELECT i.id FROM institucion i JOIN territorio t ON t.id = i.territorio_id
            WHERE ({0}::uuid IS NULL OR i.id = {0}) AND (t.ruta = {1} OR t.ruta LIKE {1} || '/%'))
        """;

    private sealed class FilaCaptacion
    {
        public string Periodo { get; set; } = "";
        public long Donaciones { get; set; }
        public long UnidadesGeneradas { get; set; }
        public long UnidadesNoAptas { get; set; }
        public long UnidadesVencidas { get; set; }
    }

    private sealed class FilaExistencia
    {
        public Guid InstitucionId { get; set; }
        public string Componente { get; set; } = "";
        public string GrupoSanguineo { get; set; } = "";
        public long UnidadesDisponibles { get; set; }
        public long UnidadesPorVencer { get; set; }
        public bool BajoUmbral { get; set; }
    }

    private static NpgsqlParameter[] ParametrosAlcance(FiltroIndicadores f) =>
    [
        Procesados.P("p0", NpgsqlDbType.Uuid, f.InstitucionId),
        Procesados.P("p1", NpgsqlDbType.Text, f.Alcance.Valor),
    ];

    public async Task<IReadOnlyList<PeriodoCaptacion>> CaptacionAsync(FiltroIndicadores f, CancellationToken ct)
    {
        // Semana ISO (2026-W44), mes (2026-10) o día (2026-10-28): el texto ordena igual que el tiempo.
        var formato = f.AgruparPor switch { "dia" => "YYYY-MM-DD", "semana" => "IYYY-\"W\"IW", _ => "YYYY-MM" };
        var filas = await contexto.Database.SqlQueryRaw<FilaCaptacion>(Alcance + """
            SELECT to_char(c.dia, {2}) AS "Periodo",
                   sum(c.donaciones)::bigint AS "Donaciones", sum(c.unidades_generadas)::bigint AS "UnidadesGeneradas",
                   sum(c.unidades_no_aptas)::bigint AS "UnidadesNoAptas", sum(c.unidades_vencidas)::bigint AS "UnidadesVencidas"
            FROM captacion_diaria c JOIN alcance a ON a.id = c.institucion_id
            WHERE c.dia BETWEEN {3} AND {4}
            GROUP BY 1 ORDER BY 1
            """, [.. ParametrosAlcance(f), Procesados.P("p2", NpgsqlDbType.Text, formato),
                Procesados.P("p3", NpgsqlDbType.Date, f.Desde), Procesados.P("p4", NpgsqlDbType.Date, f.Hasta)])
            .ToListAsync(ct);
        return filas.Select(x =>
        {
            var suprimido = x.Donaciones < MinimoParaMostrarNoAptas;
            return new PeriodoCaptacion(x.Periodo, x.Donaciones, x.UnidadesGeneradas, suprimido ? null : x.UnidadesNoAptas,
                x.UnidadesVencidas, suprimido);
        }).ToList();
    }

    public async Task<IReadOnlyList<ExistenciaAgregada>> InventarioAsync(FiltroIndicadores f, CancellationToken ct)
    {
        var filas = await contexto.Database.SqlQueryRaw<FilaExistencia>(Alcance + """
            SELECT e.institucion_id AS "InstitucionId", e.componente AS "Componente", e.grupo_sanguineo AS "GrupoSanguineo",
                   e.unidades_disponibles::bigint AS "UnidadesDisponibles", e.unidades_por_vencer::bigint AS "UnidadesPorVencer",
                   e.bajo_umbral AS "BajoUmbral"
            FROM existencia_agregada e JOIN alcance a ON a.id = e.institucion_id
            ORDER BY e.institucion_id, e.componente, e.grupo_sanguineo
            """, ParametrosAlcance(f)).ToListAsync(ct);
        return filas.Select(x => new ExistenciaAgregada(x.InstitucionId, x.Componente, x.GrupoSanguineo,
            x.UnidadesDisponibles, x.UnidadesPorVencer, x.BajoUmbral)).ToList();
    }

    public async Task<DateTimeOffset?> ActualizadoEnAsync(FiltroIndicadores f, CancellationToken ct) =>
        await contexto.Database.SqlQueryRaw<DateTimeOffset?>(Alcance + """
            SELECT min(r.generado_en) AS "Value" FROM indicador_recibido r JOIN alcance a ON a.id = r.institucion_id
            """, ParametrosAlcance(f)).SingleAsync(ct);

    public async Task<bool> ReemplazarAsync(Guid eventoId, FotografiaIndicadores foto, CancellationToken ct)
    {
        await using var transaccion = await contexto.Database.BeginTransactionAsync(ct);
        if (!await Procesados.RegistrarAsync(contexto, eventoId, "indicators.updated", ct))
        {
            await transaccion.CommitAsync(ct);
            return false;
        }

        // Solo una fotografía más nueva que la vigente la reemplaza.
        var ahora = DateTimeOffset.UtcNow;
        var vigente = await contexto.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO indicador_recibido (institucion_id, generado_en, recibido_en)
            VALUES ({foto.InstitucionId}, {foto.GeneradoEn.ToUniversalTime()}, {ahora})
            ON CONFLICT (institucion_id) DO UPDATE SET generado_en = EXCLUDED.generado_en, recibido_en = EXCLUDED.recibido_en
            WHERE indicador_recibido.generado_en < EXCLUDED.generado_en
            """, ct) == 1;
        if (vigente)
        {
            await contexto.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM captacion_diaria WHERE institucion_id = {foto.InstitucionId}", ct);
            await contexto.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM existencia_agregada WHERE institucion_id = {foto.InstitucionId}", ct);
            var c = foto.Captacion;
            await contexto.Database.ExecuteSqlRawAsync("""
                INSERT INTO captacion_diaria (institucion_id, dia, donaciones, unidades_generadas, unidades_no_aptas, unidades_vencidas)
                SELECT {0}, d, n, g, na, v FROM unnest({1}, {2}, {3}, {4}, {5}) AS x(d, n, g, na, v)
                """, [Procesados.P("p0", NpgsqlDbType.Uuid, foto.InstitucionId),
                    Procesados.P("p1", NpgsqlDbType.Array | NpgsqlDbType.Date, c.Select(x => x.Dia).ToArray()),
                    Procesados.P("p2", NpgsqlDbType.Array | NpgsqlDbType.Integer, c.Select(x => x.Donaciones).ToArray()),
                    Procesados.P("p3", NpgsqlDbType.Array | NpgsqlDbType.Integer, c.Select(x => x.UnidadesGeneradas).ToArray()),
                    Procesados.P("p4", NpgsqlDbType.Array | NpgsqlDbType.Integer, c.Select(x => x.UnidadesNoAptas).ToArray()),
                    Procesados.P("p5", NpgsqlDbType.Array | NpgsqlDbType.Integer, c.Select(x => x.UnidadesVencidas).ToArray())], ct);
            var e = foto.Inventario;
            await contexto.Database.ExecuteSqlRawAsync("""
                INSERT INTO existencia_agregada (institucion_id, componente, grupo_sanguineo, unidades_disponibles, unidades_por_vencer, bajo_umbral)
                SELECT {0}, c, g, d, pv, b FROM unnest({1}, {2}, {3}, {4}, {5}) AS x(c, g, d, pv, b)
                """, [Procesados.P("p0", NpgsqlDbType.Uuid, foto.InstitucionId),
                    Procesados.P("p1", NpgsqlDbType.Array | NpgsqlDbType.Text, e.Select(x => x.Componente).ToArray()),
                    Procesados.P("p2", NpgsqlDbType.Array | NpgsqlDbType.Text, e.Select(x => x.GrupoSanguineo).ToArray()),
                    Procesados.P("p3", NpgsqlDbType.Array | NpgsqlDbType.Integer, e.Select(x => x.UnidadesDisponibles).ToArray()),
                    Procesados.P("p4", NpgsqlDbType.Array | NpgsqlDbType.Integer, e.Select(x => x.UnidadesPorVencer).ToArray()),
                    Procesados.P("p5", NpgsqlDbType.Array | NpgsqlDbType.Boolean, e.Select(x => x.BajoUmbral).ToArray())], ct);
        }
        await transaccion.CommitAsync(ct);
        return true;
    }
}

/// <summary>Anexa a registro_bitacora los registros que llegan por EV-10.</summary>
public sealed class BitacoraRecibida(InstitucionalDbContext contexto) : IProyeccionBitacora
{
    public async Task<bool> AnexarAsync(Guid eventoId, string ctx, RegistroAuditoria r, CancellationToken ct)
    {
        await using var transaccion = await contexto.Database.BeginTransactionAsync(ct);
        if (!await Procesados.RegistrarAsync(contexto, eventoId, "audit.recorded", ct))
        {
            await transaccion.CommitAsync(ct);
            return false;
        }
        // Un mismo registro reenviado con otro evento tampoco se duplica: la clave es (contexto, id).
        await contexto.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO registro_bitacora (contexto, id, actor_tipo, actor_id, rol, jurisdiccion_solicitada, operacion,
                                           recurso_tipo, recurso_id, resultado, correlacion_id, origen, ocurrido_en)
            VALUES ({ctx}, {r.Id}, {r.ActorTipo}, {Recortar(r.ActorId, 64)}, {Recortar(r.Rol, 40)},
                    {Recortar(r.JurisdiccionSolicitada, 80)}, {Recortar(r.Operacion, 80)}, {Recortar(r.RecursoTipo, 40)},
                    {r.RecursoId}, {r.Resultado}, {Recortar(r.CorrelacionId, 36)}, {Recortar(r.Origen, 45)},
                    {r.OcurridoEn.ToUniversalTime()})
            ON CONFLICT (contexto, id) DO NOTHING
            """, ct);
        await transaccion.CommitAsync(ct);
        return true;
    }

    private static string? Recortar(string? valor, int maximo) => valor is { Length: var n } && n > maximo ? valor[..maximo] : valor;
}

/// <summary>Una serie remota, leída de su copia local con el mismo contrato que la propia.</summary>
public sealed class FuenteAuditoriaRecibida(string contextoSerie, InstitucionalDbContext contexto) : IFuenteAuditoria
{
    public string Contexto => contextoSerie;

    public async Task<IReadOnlyList<RegistroAuditoria>> ConsultarAsync(FiltroBitacora f, CancellationToken ct)
    {
        var filas = await SerieAuditoria.Filtrar(contexto.Auditoria, f, "registro_bitacora", contextoSerie).ToListAsync(ct);
        return filas.Select(x => x.ComoRegistro()).ToList();
    }

    public async Task<DateTimeOffset?> AlDiaHastaAsync(CancellationToken ct) =>
        await contexto.Database.SqlQuery<DateTimeOffset?>(
            $"""SELECT max(recibido_en) AS "Value" FROM registro_bitacora WHERE contexto = {contextoSerie}""").SingleAsync(ct);
}
