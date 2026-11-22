// RedVital — institutional-service. Adaptadores de persistencia de los puertos
// de Aplicacion, sobre EF Core y PostgreSQL.

using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RedVital.Institucional.Aplicacion;
using RedVital.Institucional.Aplicacion.Puertos;
using RedVital.Institucional.Dominio;

namespace RedVital.Institucional.Infraestructura.Persistencia;

internal static class Claves
{
    /// <summary>23505: violación de una clave única.</summary>
    public static async Task GuardarAsync(DbContext contexto, CancellationToken ct)
    {
        try
        {
            await contexto.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new Duplicado();
        }
    }

    /// <summary>Patrón LIKE para «la ruta o cualquiera de sus descendientes».</summary>
    public static string Descendientes(string ruta) => ruta + "/%";
}

public sealed class RepositorioTerritorios(InstitucionalDbContext contexto) : IRepositorioTerritorios
{
    public Task<Territorio?> PorIdAsync(Guid id, CancellationToken ct) =>
        contexto.Territorios.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<Territorio?> PorCodigoAsync(string codigoDivipola, CancellationToken ct) =>
        contexto.Territorios.AsNoTracking().FirstOrDefaultAsync(t => t.CodigoDivipola == codigoDivipola, ct);

    public async Task<Paginado<Territorio>> ListarAsync(FiltroTerritorios f, CancellationToken ct)
    {
        var baseRuta = f.Base.Valor;
        var debajo = Claves.Descendientes(baseRuta);
        var q = contexto.Territorios.AsNoTracking().Where(t => t.Ruta == baseRuta || EF.Functions.Like(t.Ruta, debajo));
        if (f.Nivel is { } nivel) q = q.Where(t => t.Nivel == nivel);
        if (f.PadreId is { } padre) q = q.Where(t => t.PadreId == padre);
        var total = await q.LongCountAsync(ct);
        var elementos = await q.OrderBy(t => t.Ruta).Skip((f.Pagina - 1) * f.Tamano).Take(f.Tamano).ToListAsync(ct);
        return new Paginado<Territorio>(elementos, total, f.Pagina, f.Tamano);
    }

    public async Task AgregarAsync(Territorio territorio, CancellationToken ct)
    {
        contexto.Territorios.Add(territorio);
        await Claves.GuardarAsync(contexto, ct);
    }
}

public sealed class RepositorioInstituciones(InstitucionalDbContext contexto) : IRepositorioInstituciones
{
    public Task<Institucion?> PorIdAsync(Guid id, CancellationToken ct) =>
        contexto.Instituciones.FirstOrDefaultAsync(i => i.Id == id, ct);

    public Task<VistaInstitucion?> VistaAsync(Guid id, CancellationToken ct) =>
        Vistas(Unidas().Where(x => x.I.Id == id)).FirstOrDefaultAsync(ct);

    public async Task<Paginado<VistaInstitucion>> ListarAsync(FiltroInstituciones f, CancellationToken ct)
    {
        // Se filtra sobre las entidades unidas y se proyecta al final: EF no
        // traduce un filtro sobre el constructor de la vista.
        var q = Unidas();
        if (!f.IncluirBajas) q = q.Where(x => x.I.Vigente);
        if (f.SoloId is { } id) q = q.Where(x => x.I.Id == id);
        if (f.Tipo is { } tipo) q = q.Where(x => x.I.Tipo == tipo);
        if (f.Ruta is { } ruta)
        {
            var valor = ruta.Valor;
            var debajo = Claves.Descendientes(valor);
            q = f.IncluirDescendientes
                ? q.Where(x => x.T.Ruta == valor || EF.Functions.Like(x.T.Ruta, debajo))
                : q.Where(x => x.T.Ruta == valor);
        }
        var total = await q.LongCountAsync(ct);
        var elementos = await Vistas(q.OrderBy(x => x.T.Ruta).ThenBy(x => x.I.Nombre)
            .Skip((f.Pagina - 1) * f.Tamano).Take(f.Tamano)).ToListAsync(ct);
        return new Paginado<VistaInstitucion>(elementos, total, f.Pagina, f.Tamano);
    }

    public async Task AgregarAsync(Institucion institucion, CancellationToken ct)
    {
        contexto.Instituciones.Add(institucion);
        await Claves.GuardarAsync(contexto, ct);
    }

    public Task GuardarAsync(Institucion institucion, CancellationToken ct) => Claves.GuardarAsync(contexto, ct);

    private sealed class Fila
    {
        public required Institucion I { get; init; }
        public required Territorio T { get; init; }
    }

    private IQueryable<Fila> Unidas() =>
        from i in contexto.Instituciones.AsNoTracking()
        join t in contexto.Territorios.AsNoTracking() on i.TerritorioId equals t.Id
        select new Fila { I = i, T = t };

    private static IQueryable<VistaInstitucion> Vistas(IQueryable<Fila> filas) =>
        filas.Select(x => new VistaInstitucion(x.I.Id, x.I.Codigo, x.I.Nombre,
            x.I.Tipo == TipoInstitucion.BancoDeSangre ? "banco_de_sangre" : "servicio_transfusion",
            x.T.Id, x.T.CodigoDivipola, x.T.Ruta, x.T.Nombre, x.I.Direccion, x.I.Vigente, x.I.CreadoEn));
}

/// <summary>
/// Cobertura territorial: con la escala de la red (decenas de departamentos y
/// de instituciones) se agrega en memoria sobre una sola lectura de cada tabla.
/// </summary>
public sealed class ConsultaCobertura(InstitucionalDbContext contexto) : IConsultaCobertura
{
    public async Task<IReadOnlyList<CoberturaDepartamento>> DentroDeAsync(RutaTerritorial ruta, CancellationToken ct)
    {
        var valor = ruta.Valor;
        var debajo = Claves.Descendientes(valor);
        // Los departamentos que tocan la ruta: los de dentro, o el que la contiene.
        var departamentos = await contexto.Territorios.AsNoTracking()
            .Where(t => t.Nivel == NivelTerritorial.Departamental &&
                        (t.Ruta == valor || EF.Functions.Like(t.Ruta, debajo) || EF.Functions.Like(valor, t.Ruta + "/%")))
            .OrderBy(t => t.Nombre).ToListAsync(ct);
        var municipios = await contexto.Territorios.AsNoTracking()
            .Where(t => t.Nivel == NivelTerritorial.Municipal && t.Vigente && (t.Ruta == valor || EF.Functions.Like(t.Ruta, debajo)))
            .Select(t => new { t.Id, t.Ruta }).ToListAsync(ct);
        var idsMunicipios = municipios.Select(m => m.Id).ToList();
        var instituciones = await contexto.Instituciones.AsNoTracking()
            .Where(i => i.Vigente && idsMunicipios.Contains(i.TerritorioId))
            .Select(i => new { i.TerritorioId, i.Tipo }).ToListAsync(ct);
        var rutaDe = municipios.ToDictionary(m => m.Id, m => m.Ruta);

        return departamentos.Select(d =>
        {
            var prefijo = d.Ruta + "/";
            var propios = municipios.Where(m => m.Ruta.StartsWith(prefijo, StringComparison.Ordinal)).Select(m => m.Id).ToHashSet();
            var suyas = instituciones.Where(i => propios.Contains(i.TerritorioId)).ToList();
            return new CoberturaDepartamento(d.CodigoDivipola, d.Nombre, d.Ruta, propios.Count,
                suyas.Where(i => i.Tipo == TipoInstitucion.BancoDeSangre).Select(i => i.TerritorioId).Distinct().Count(),
                suyas.Count(i => i.Tipo == TipoInstitucion.BancoDeSangre),
                suyas.Count(i => i.Tipo == TipoInstitucion.ServicioTransfusion));
        }).ToList();
    }
}

public sealed class RegistroAuditoriaInst(InstitucionalDbContext contexto) : IRegistroAuditoria
{
    public async Task RegistrarAsync(RegistroAuditoria r, CancellationToken ct)
    {
        contexto.Auditoria.Add(new FilaAuditoria
        {
            Id = r.Id, ActorTipo = r.ActorTipo, ActorId = Recortar(r.ActorId, 64), Rol = Recortar(r.Rol, 40),
            JurisdiccionSolicitada = Recortar(r.JurisdiccionSolicitada, 80), Operacion = r.Operacion,
            RecursoTipo = r.RecursoTipo, RecursoId = r.RecursoId, Resultado = r.Resultado,
            CorrelacionId = r.CorrelacionId, Origen = Recortar(r.Origen, 45), OcurridoEn = r.OcurridoEn,
        });
        await contexto.SaveChangesAsync(ct);
    }

    private static string? Recortar(string? valor, int maximo) => valor is { Length: var n } && n > maximo ? valor[..maximo] : valor;
}

/// <summary>La serie propia, con el mismo contrato que las copias de las demás.</summary>
public sealed class FuenteAuditoriaPropia(InstitucionalDbContext contexto, TimeProvider reloj) : IFuenteAuditoria
{
    public string Contexto => "institucional";

    public async Task<IReadOnlyList<RegistroAuditoria>> ConsultarAsync(FiltroBitacora f, CancellationToken ct)
    {
        var filas = await SerieAuditoria.Filtrar(contexto.Auditoria, f, "registro_auditoria_inst", null).ToListAsync(ct);
        return filas.Select(x => x.ComoRegistro()).ToList();
    }

    /// <summary>Se escribe en esta misma base: está al día siempre.</summary>
    public Task<DateTimeOffset?> AlDiaHastaAsync(CancellationToken ct) => Task.FromResult<DateTimeOffset?>(reloj.GetUtcNow());
}

/// <summary>
/// Consulta común de una serie: filtros opcionales y paginación por cursor
/// (ocurrido_en, id) descendente. La comparación de filas la hace PostgreSQL,
/// con su orden de uuid, que es el mismo orden del texto del identificador.
/// </summary>
public static class SerieAuditoria
{
    private const string Columnas = "id, actor_tipo, actor_id, rol, jurisdiccion_solicitada, operacion, recurso_tipo, " +
                                    "recurso_id, resultado, correlacion_id, origen, ocurrido_en";

    /// <param name="tabla">registro_auditoria_inst o registro_bitacora; nunca viene de la petición.</param>
    /// <param name="contextoSerie">En registro_bitacora, la serie que se lee.</param>
    public static IQueryable<FilaAuditoria> Filtrar(DbSet<FilaAuditoria> serie, FiltroBitacora f, string tabla, string? contextoSerie) =>
        serie.FromSqlRaw($$"""
            SELECT {{Columnas}} FROM {{tabla}}
            WHERE ({0}::text IS NULL OR recurso_tipo = {0})
              AND ({1}::uuid IS NULL OR recurso_id = {1})
              AND ({2}::text IS NULL OR correlacion_id = {2})
              AND ({3}::text IS NULL OR actor_id = {3})
              AND ({4}::timestamptz IS NULL OR ocurrido_en >= {4})
              AND ({5}::timestamptz IS NULL OR ocurrido_en <= {5})
              AND ({6}::timestamptz IS NULL OR (ocurrido_en, id) < ({6}, {7}::uuid))
              {{(contextoSerie is null ? "" : "AND contexto = {9}")}}
            ORDER BY ocurrido_en DESC, id DESC
            LIMIT {8}
            """,
            Parametro("p0", NpgsqlDbType.Text, f.RecursoTipo), Parametro("p1", NpgsqlDbType.Uuid, f.RecursoId),
            Parametro("p2", NpgsqlDbType.Text, f.CorrelacionId), Parametro("p3", NpgsqlDbType.Text, f.ActorId),
            Parametro("p4", NpgsqlDbType.TimestampTz, f.Desde?.ToUniversalTime()),
            Parametro("p5", NpgsqlDbType.TimestampTz, f.Hasta?.ToUniversalTime()),
            Parametro("p6", NpgsqlDbType.TimestampTz, f.AntesDeEn?.ToUniversalTime()),
            Parametro("p7", NpgsqlDbType.Uuid, f.AntesDeId ?? Guid.Empty),
            Parametro("p8", NpgsqlDbType.Integer, f.Tamano),
            Parametro("p9", NpgsqlDbType.Text, contextoSerie ?? ""))
            .AsNoTracking();

    /// <summary>Con tipo explícito: un nulo sin tipo no se compararía con uuid ni con timestamptz.</summary>
    private static NpgsqlParameter Parametro(string nombre, NpgsqlDbType tipo, object? valor) =>
        new(nombre, tipo) { Value = valor ?? DBNull.Value };
}
