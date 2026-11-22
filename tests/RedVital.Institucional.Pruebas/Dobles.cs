// Dobles en memoria de los puertos de Aplicacion, para probar los casos de
// uso sin base ni broker (los adaptadores reales tienen sus propias pruebas).

using RedVital.Institucional.Aplicacion;
using RedVital.Institucional.Aplicacion.CasosDeUso;
using RedVital.Institucional.Aplicacion.Puertos;
using RedVital.Institucional.Dominio;

namespace RedVital.Institucional.Pruebas;

public static class Fabrica
{
    /// <summary>Un territorio como lo cargaría la base.</summary>
    public static Territorio Territorio(string codigo, string nombre, NivelTerritorial nivel, Guid? padre)
    {
        var t = (Territorio)Activator.CreateInstance(typeof(Territorio), nonPublic: true)!;
        Fijar(t, nameof(Dominio.Territorio.Id), Guid.NewGuid());
        Fijar(t, nameof(Dominio.Territorio.CodigoDivipola), codigo);
        Fijar(t, nameof(Dominio.Territorio.Nombre), nombre);
        Fijar(t, nameof(Dominio.Territorio.Nivel), nivel);
        Fijar(t, nameof(Dominio.Territorio.PadreId), padre);
        Fijar(t, nameof(Dominio.Territorio.Ruta), RutaTerritorial.DeCodigo(codigo).Valor);
        return t;
    }

    private static void Fijar(object o, string propiedad, object? valor) => o.GetType().GetProperty(propiedad)!.SetValue(o, valor);

    public static ContextoPeticion Ctx(string rol, string? jurisdiccion, string? sub = null) =>
        new(new Solicitante(sub ?? Guid.NewGuid().ToString(), rol, jurisdiccion), Guid.NewGuid().ToString(), "10.0.0.1");
}

/// <summary>Mini red: Colombia, Antioquia (Medellín, Bello), Bogotá D.C. (Bogotá), con tres instituciones.</summary>
public sealed class Red : IRepositorioTerritorios, IRepositorioInstituciones, IConsultaCobertura, IRegistroAuditoria, IBandejaSalida
{
    public readonly List<Territorio> Territorios = [];
    public readonly List<Institucion> Instituciones = [];
    public readonly List<RegistroAuditoria> Auditoria = [];
    public readonly List<(InstitucionCambiada Evento, string CorrelacionId)> Bandeja = [];
    public readonly Territorio Nacion, Antioquia, Medellin, Bello, BogotaDc, Bogota;
    public readonly Institucion Aurora, Ceiba, Sabana;

    public Red()
    {
        Nacion = Fabrica.Territorio("00", "Colombia", NivelTerritorial.Nacional, null);
        Antioquia = Fabrica.Territorio("05", "Antioquia", NivelTerritorial.Departamental, Nacion.Id);
        Medellin = Fabrica.Territorio("05001", "Medellín", NivelTerritorial.Municipal, Antioquia.Id);
        Bello = Fabrica.Territorio("05088", "Bello", NivelTerritorial.Municipal, Antioquia.Id);
        BogotaDc = Fabrica.Territorio("11", "Bogotá, D.C.", NivelTerritorial.Departamental, Nacion.Id);
        Bogota = Fabrica.Territorio("11001", "Bogotá, D.C.", NivelTerritorial.Municipal, BogotaDc.Id);
        Territorios.AddRange([Nacion, Antioquia, Medellin, Bello, BogotaDc, Bogota]);
        var ahora = DateTimeOffset.UtcNow;
        Aurora = Institucion.Crear("QA-0001", "Banco Aurora", TipoInstitucion.BancoDeSangre, Medellin, null, Guid.NewGuid(), ahora);
        Ceiba = Institucion.Crear("QA-0002", "Banco Ceiba", TipoInstitucion.BancoDeSangre, Bello, null, Guid.NewGuid(), ahora);
        Sabana = Institucion.Crear("QA-0003", "Banco Sabana", TipoInstitucion.ServicioTransfusion, Bogota, null, Guid.NewGuid(), ahora);
        Instituciones.AddRange([Aurora, Ceiba, Sabana]);
    }

    private Territorio TerritorioDe(Institucion i) => Territorios.Single(t => t.Id == i.TerritorioId);

    private VistaInstitucion Vista(Institucion i)
    {
        var t = TerritorioDe(i);
        return new(i.Id, i.Codigo, i.Nombre, Tipos.Codigo(i.Tipo), t.Id, t.CodigoDivipola, t.Ruta, t.Nombre, i.Direccion, i.Vigente, i.CreadoEn);
    }

    // ---------------- territorios
    public Task<Territorio?> PorIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Territorios.FirstOrDefault(t => t.Id == id));
    public Task<Territorio?> PorCodigoAsync(string c, CancellationToken ct) => Task.FromResult(Territorios.FirstOrDefault(t => t.CodigoDivipola == c));
    public Task<Paginado<Territorio>> ListarAsync(FiltroTerritorios f, CancellationToken ct)
    {
        var q = Territorios.Where(t => f.Base.Contiene(t.RutaTerritorial) && (f.Nivel is null || t.Nivel == f.Nivel) && (f.PadreId is null || t.PadreId == f.PadreId)).ToList();
        return Task.FromResult(new Paginado<Territorio>(q.Skip((f.Pagina - 1) * f.Tamano).Take(f.Tamano).ToList(), q.Count, f.Pagina, f.Tamano));
    }
    public Task AgregarAsync(Territorio t, CancellationToken ct)
    {
        if (Territorios.Any(x => x.CodigoDivipola == t.CodigoDivipola)) throw new Duplicado();
        Territorios.Add(t);
        return Task.CompletedTask;
    }

    // ---------------- instituciones
    Task<Institucion?> IRepositorioInstituciones.PorIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Instituciones.FirstOrDefault(i => i.Id == id));
    public Task<VistaInstitucion?> VistaAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Instituciones.FirstOrDefault(i => i.Id == id) is { } i ? Vista(i) : null);
    public Task<Paginado<VistaInstitucion>> ListarAsync(FiltroInstituciones f, CancellationToken ct)
    {
        var q = Instituciones.Select(Vista).Where(v =>
            (f.IncluirBajas || v.Vigente) && (f.SoloId is null || v.Id == f.SoloId) &&
            (f.Tipo is null || v.Tipo == Tipos.Codigo(f.Tipo.Value)) &&
            (f.Ruta is null || (f.IncluirDescendientes
                ? RutaTerritorial.TryParse(v.TerritorioRuta, out var r) && f.Ruta.Contiene(r)
                : v.TerritorioRuta == f.Ruta.Valor))).ToList();
        return Task.FromResult(new Paginado<VistaInstitucion>(q.Skip((f.Pagina - 1) * f.Tamano).Take(f.Tamano).ToList(), q.Count, f.Pagina, f.Tamano));
    }
    public Task AgregarAsync(Institucion i, CancellationToken ct)
    {
        if (Instituciones.Any(x => x.Codigo == i.Codigo)) throw new Duplicado();
        Instituciones.Add(i);
        return Task.CompletedTask;
    }
    public Task GuardarAsync(Institucion i, CancellationToken ct) => Task.CompletedTask;

    // ---------------- cobertura y auditoría
    public Task<IReadOnlyList<CoberturaDepartamento>> DentroDeAsync(RutaTerritorial ruta, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CoberturaDepartamento>>([new CoberturaDepartamento("05", "Antioquia", "/00/05", 2, 2, 2, 0)]);
    public Task RegistrarAsync(RegistroAuditoria r, CancellationToken ct)
    {
        Auditoria.Add(r);
        return Task.CompletedTask;
    }

    public void Encolar(InstitucionCambiada evento, string correlacionId) => Bandeja.Add((evento, correlacionId));

    // ---------------- casos de uso cableados sobre esta red
    public ResolutorJurisdiccion Jurisdiccion => new(this);
    public Denegaciones Denegaciones => new(this, TimeProvider.System);
    public ConsultarInstituciones Consultar => new(this, this, Jurisdiccion, Denegaciones);
    public CrearInstitucion Crear => new(this, this, Jurisdiccion, Denegaciones, this, TimeProvider.System);
    public ModificarInstitucion Modificar => new(this, Jurisdiccion, Denegaciones, this, TimeProvider.System);
    public ConsultarTerritorios Territorios_ => new(this, Jurisdiccion, Denegaciones);
    public CrearTerritorio CrearTerritorio => new(this, Denegaciones);
}

public sealed class FuenteFija(string contexto, params RegistroAuditoria[] registros) : IFuenteAuditoria
{
    public string Contexto => contexto;
    public DateTimeOffset? AlDiaHasta { get; init; }
    public FiltroBitacora? Recibido { get; private set; }

    public Task<IReadOnlyList<RegistroAuditoria>> ConsultarAsync(FiltroBitacora f, CancellationToken ct)
    {
        Recibido = f;
        IReadOnlyList<RegistroAuditoria> r = registros
            .Where(x => f.AntesDeEn is null || x.OcurridoEn < f.AntesDeEn ||
                        (x.OcurridoEn == f.AntesDeEn && string.CompareOrdinal(x.Id.ToString(), f.AntesDeId.ToString()) < 0))
            .OrderByDescending(x => x.OcurridoEn).ThenByDescending(x => x.Id.ToString(), StringComparer.Ordinal)
            .Take(f.Tamano).ToList();
        return Task.FromResult(r);
    }

    public Task<DateTimeOffset?> AlDiaHastaAsync(CancellationToken ct) => Task.FromResult(AlDiaHasta);
}

public sealed class IndicadoresFijos : IConsultaIndicadores
{
    public static readonly DateTimeOffset Fotografia = new(2026, 10, 31, 23, 0, 0, TimeSpan.Zero);
    public FiltroIndicadores? Recibido { get; private set; }

    public Task<IReadOnlyList<PeriodoCaptacion>> CaptacionAsync(FiltroIndicadores f, CancellationToken ct)
    {
        Recibido = f;
        return Task.FromResult<IReadOnlyList<PeriodoCaptacion>>([new PeriodoCaptacion("2026-10", 3, 6, null, 0, true)]);
    }

    public Task<IReadOnlyList<ExistenciaAgregada>> InventarioAsync(FiltroIndicadores f, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ExistenciaAgregada>>([]);

    public Task<DateTimeOffset?> ActualizadoEnAsync(FiltroIndicadores f, CancellationToken ct) => Task.FromResult<DateTimeOffset?>(Fotografia);
}
