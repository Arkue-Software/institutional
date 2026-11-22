// RedVital — institutional-service
// Puertos de salida, uno por necesidad (segregación de interfaces): un caso de
// uso que solo lee territorios no conoce la escritura de instituciones ni Kafka.

using RedVital.Institucional.Dominio;

namespace RedVital.Institucional.Aplicacion.Puertos;

public sealed record FiltroTerritorios(RutaTerritorial Base, NivelTerritorial? Nivel, Guid? PadreId, int Pagina, int Tamano);

public interface IRepositorioTerritorios
{
    Task<Territorio?> PorIdAsync(Guid id, CancellationToken ct);
    Task<Territorio?> PorCodigoAsync(string codigoDivipola, CancellationToken ct);
    Task<Paginado<Territorio>> ListarAsync(FiltroTerritorios filtro, CancellationToken ct);
    /// <summary>Lanza <see cref="Duplicado"/> si el código ya existe.</summary>
    Task AgregarAsync(Territorio territorio, CancellationToken ct);
}

/// <summary>Institución con su territorio: la forma que consume la web (DD 13.2).</summary>
public sealed record VistaInstitucion(
    Guid Id, string Codigo, string Nombre, string Tipo, Guid TerritorioId, string TerritorioCodigo,
    string TerritorioRuta, string TerritorioNombre, string? Direccion, bool Vigente, DateTimeOffset CreadoEn);

/// <summary>
/// <paramref name="Ruta"/> nula no filtra por territorio. Con
/// <paramref name="IncluirDescendientes"/> falso, solo las del territorio exacto.
/// </summary>
public sealed record FiltroInstituciones(RutaTerritorial? Ruta, bool IncluirDescendientes, TipoInstitucion? Tipo,
    Guid? SoloId, bool IncluirBajas, int Pagina, int Tamano);

public interface IRepositorioInstituciones
{
    Task<Institucion?> PorIdAsync(Guid id, CancellationToken ct);
    Task<VistaInstitucion?> VistaAsync(Guid id, CancellationToken ct);
    Task<Paginado<VistaInstitucion>> ListarAsync(FiltroInstituciones filtro, CancellationToken ct);
    /// <summary>Lanza <see cref="Duplicado"/> si el código de habilitación ya existe.</summary>
    Task AgregarAsync(Institucion institucion, CancellationToken ct);
    Task GuardarAsync(Institucion institucion, CancellationToken ct);
}

/// <summary>Cobertura territorial (RF-13): cuántos municipios de cada departamento tienen un banco vigente.</summary>
public sealed record CoberturaDepartamento(
    string TerritorioCodigo, string Nombre, string Ruta, int Municipios, int MunicipiosConBanco,
    int BancosDeSangre, int ServiciosTransfusion);

public interface IConsultaCobertura
{
    Task<IReadOnlyList<CoberturaDepartamento>> DentroDeAsync(RutaTerritorial ruta, CancellationToken ct);
}

public interface IRegistroAuditoria
{
    Task RegistrarAsync(RegistroAuditoria registro, CancellationToken ct);
}

/// <summary>Filtro común de las cuatro series. El cursor es el último registro de la página anterior.</summary>
public sealed record FiltroBitacora(
    string? RecursoTipo, Guid? RecursoId, string? CorrelacionId, string? ActorId,
    DateTimeOffset? Desde, DateTimeOffset? Hasta, int Tamano, DateTimeOffset? AntesDeEn, Guid? AntesDeId);

/// <summary>
/// Una serie de la bitácora (ADR-016). La propia y las copias de Identidad,
/// Campañas y Donación, que llegan por Kafka (EV-10), implementan el mismo
/// contrato: el compositor no distingue entre ellas, y agregar una serie es
/// registrar otra fuente.
/// </summary>
public interface IFuenteAuditoria
{
    string Contexto { get; }

    /// <summary>Hasta <c>filtro.Tamano</c> registros, de más reciente a más antiguo.</summary>
    Task<IReadOnlyList<RegistroAuditoria>> ConsultarAsync(FiltroBitacora filtro, CancellationToken ct);

    /// <summary>Hasta cuándo está al día la serie: la serie propia, siempre; una copia, hasta su último registro recibido.</summary>
    Task<DateTimeOffset?> AlDiaHastaAsync(CancellationToken ct);
}

/// <summary>Lo que se mide: una institución, o todas las de <paramref name="Alcance"/>.</summary>
public sealed record FiltroIndicadores(DateOnly Desde, DateOnly Hasta, string AgruparPor, Guid? InstitucionId, RutaTerritorial Alcance);

public sealed record PeriodoCaptacion(
    string Periodo, long Donaciones, long UnidadesGeneradas, long? UnidadesNoAptas, long UnidadesVencidas,
    bool SuprimidoPorAgregacion);

public sealed record ExistenciaAgregada(
    Guid InstitucionId, string Componente, string GrupoSanguineo, long UnidadesDisponibles, long UnidadesPorVencer,
    bool BajoUmbral);

/// <summary>
/// Lectura de M7 sobre la copia local de los agregados que publica Donación
/// (EV-09). Solo conteos, nunca datos de donantes (RI-03).
/// </summary>
public interface IConsultaIndicadores
{
    Task<IReadOnlyList<PeriodoCaptacion>> CaptacionAsync(FiltroIndicadores filtro, CancellationToken ct);
    Task<IReadOnlyList<ExistenciaAgregada>> InventarioAsync(FiltroIndicadores filtro, CancellationToken ct);
    /// <summary>Fecha de la fotografía más antigua entre las instituciones medidas; nula si ninguna ha llegado.</summary>
    Task<DateTimeOffset?> ActualizadoEnAsync(FiltroIndicadores filtro, CancellationToken ct);
}

/// <summary>Un día de captación de una institución, como llega en EV-09.</summary>
public sealed record DiaCaptacion(DateOnly Dia, int Donaciones, int UnidadesGeneradas, int UnidadesNoAptas, int UnidadesVencidas);

/// <summary>Existencias de una institución por componente y grupo, como llegan en EV-09.</summary>
public sealed record ExistenciaRecibida(string Componente, string GrupoSanguineo, int UnidadesDisponibles, int UnidadesPorVencer, bool BajoUmbral);

/// <summary>EV-09 completo: la fotografía vigente de una institución reemplaza a la anterior.</summary>
public sealed record FotografiaIndicadores(Guid InstitucionId, DateTimeOffset GeneradoEn,
    IReadOnlyList<DiaCaptacion> Captacion, IReadOnlyList<ExistenciaRecibida> Inventario);

public interface IProyeccionIndicadores
{
    /// <summary>Una fotografía más antigua que la guardada no la reemplaza.</summary>
    /// <returns>Falso si el evento ya se había aplicado.</returns>
    Task<bool> ReemplazarAsync(Guid eventoId, FotografiaIndicadores fotografia, CancellationToken ct);
}

public interface IProyeccionBitacora
{
    /// <returns>Falso si el evento ya se había aplicado.</returns>
    Task<bool> AnexarAsync(Guid eventoId, string contexto, RegistroAuditoria registro, CancellationToken ct);
}

/// <summary>EV-07 institution.changed: el estado completo de la institución tras el alta o el cambio.</summary>
public sealed record InstitucionCambiada(
    Guid InstitucionId, string Codigo, string Nombre, string Tipo, string TerritorioCodigo, string TerritorioRuta,
    bool Vigente, DateTimeOffset CambiadoEn);

/// <summary>
/// Bandeja de salida (DD 5.3.6). El evento queda pendiente junto con el
/// cambio: lo confirma el mismo guardado del repositorio, en la misma
/// transacción, y un publicador lo lleva a Kafka después.
/// </summary>
public interface IBandejaSalida
{
    void Encolar(InstitucionCambiada evento, string correlacionId);
}

/// <summary>Clave única violada (código DIVIPOLA o de habilitación repetido).</summary>
public sealed class Duplicado : Exception;
