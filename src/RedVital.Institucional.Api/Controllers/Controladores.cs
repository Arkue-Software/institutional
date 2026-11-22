// RedVital — institutional-service. Operaciones expuestas a la web por el
// gateway (DD Tabla 32). Ningún otro servicio las llama: entre servicios todo
// pasa por Kafka. Los controladores solo traducen HTTP: la regla vive en los
// casos de uso.
//
//   GET  /v1/territorios            POST /v1/territorios
//   GET  /v1/instituciones          POST /v1/instituciones
//   GET  /v1/instituciones/{id}     PUT  /v1/instituciones/{id}
//   GET  /v1/indicadores
//   GET  /v1/auditoria

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RedVital.Institucional.Api.Soporte;
using RedVital.Institucional.Aplicacion;
using RedVital.Institucional.Aplicacion.CasosDeUso;

namespace RedVital.Institucional.Api.Controllers;

public abstract class Base : ControllerBase
{
    protected IActionResult Responder<T>(Resultado<T> r, int estado = 200) =>
        !r.Exitoso ? Problemas.Desde(r, HttpContext.CorrelacionId())
        : estado == 201 ? StatusCode(201, r.Valor) : Ok(r.Valor);
}

[ApiController, Authorize, Route("v1/territorios")]
public sealed class TerritoriosController(ConsultarTerritorios consultar, CrearTerritorio crear) : Base
{
    [HttpGet]
    public async Task<IActionResult> ListarAsync([FromQuery] string? nivel, [FromQuery(Name = "padre_id")] Guid? padreId,
        [FromQuery] int? pagina, [FromQuery] int? tamano, CancellationToken ct) =>
        Responder(await consultar.EjecutarAsync(HttpContext.Contexto(), new ConsultaTerritorios(nivel, padreId, pagina, tamano), ct));

    [HttpPost]
    public async Task<IActionResult> CrearAsync([FromBody] NuevoTerritorio cuerpo, CancellationToken ct) =>
        Responder(await crear.EjecutarAsync(HttpContext.Contexto(), cuerpo, ct), 201);
}

[ApiController, Authorize, Route("v1/instituciones")]
public sealed class InstitucionesController(ConsultarInstituciones consultar, ResolverInstitucion resolver,
    CrearInstitucion crear, ModificarInstitucion modificar) : Base
{
    [HttpGet]
    public async Task<IActionResult> ListarAsync(
        [FromQuery(Name = "territorio_id")] Guid? territorioId, [FromQuery(Name = "territorio_ruta")] string? territorioRuta,
        [FromQuery(Name = "incluir_descendientes")] bool? incluirDescendientes, [FromQuery] string? tipo,
        [FromQuery(Name = "incluir_bajas")] bool? incluirBajas, [FromQuery] int? pagina, [FromQuery] int? tamano,
        CancellationToken ct) =>
        Responder(await consultar.EjecutarAsync(HttpContext.Contexto(),
            new ConsultaInstituciones(territorioId, territorioRuta, incluirDescendientes, tipo, incluirBajas, pagina, tamano), ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> ResolverAsync(Guid id, CancellationToken ct) =>
        Responder(await resolver.EjecutarAsync(HttpContext.Contexto(), id, ct));

    [HttpPost]
    public async Task<IActionResult> CrearAsync([FromBody] NuevaInstitucion cuerpo, CancellationToken ct) =>
        Responder(await crear.EjecutarAsync(HttpContext.Contexto(), cuerpo, ct), 201);

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> ModificarAsync(Guid id, [FromBody] CambioInstitucion cuerpo, CancellationToken ct) =>
        Responder(await modificar.EjecutarAsync(HttpContext.Contexto(), id, cuerpo, ct));
}

[ApiController, Authorize, Route("v1/indicadores")]
public sealed class IndicadoresController(ConsultarIndicadores consultar) : Base
{
    [HttpGet]
    public async Task<IActionResult> ConsultarAsync([FromQuery] DateOnly? desde, [FromQuery] DateOnly? hasta,
        [FromQuery(Name = "agrupar_por")] string? agruparPor, [FromQuery(Name = "institucion_id")] Guid? institucionId,
        [FromQuery(Name = "territorio_ruta")] string? territorioRuta, CancellationToken ct) =>
        Responder(await consultar.EjecutarAsync(HttpContext.Contexto(),
            new ConsultaIndicadores(desde, hasta, agruparPor, institucionId, territorioRuta), ct));
}

[ApiController, Authorize, Route("v1/auditoria")]
public sealed class AuditoriaController(ConsultarBitacora consultar) : Base
{
    /// <summary>Bitácora compuesta. Sin ninguna operación de escritura (RI-04).</summary>
    [HttpGet]
    public async Task<IActionResult> ConsultarAsync(
        [FromQuery(Name = "recurso_tipo")] string? recursoTipo, [FromQuery(Name = "recurso_id")] Guid? recursoId,
        [FromQuery(Name = "correlacion_id")] string? correlacionId, [FromQuery(Name = "actor_id")] string? actorId,
        [FromQuery] DateTimeOffset? desde, [FromQuery] DateTimeOffset? hasta, [FromQuery] int? tamano,
        [FromQuery(Name = "antes_de")] string? antesDe, CancellationToken ct)
    {
        var r = await consultar.EjecutarAsync(HttpContext.Contexto(),
            new ConsultaBitacora(recursoTipo, recursoId, correlacionId, actorId, desde, hasta, tamano, antesDe), ct);
        if (!r.Exitoso) return Problemas.Desde(r, HttpContext.CorrelacionId());
        var b = r.Valor!;
        return Ok(new
        {
            registros = b.Registros.Select(x => new
            {
                contexto = x.Contexto,
                x.Registro.Id, x.Registro.ActorTipo, x.Registro.ActorId, x.Registro.Rol, x.Registro.JurisdiccionSolicitada,
                x.Registro.Operacion, x.Registro.RecursoTipo, x.Registro.RecursoId, x.Registro.Resultado,
                x.Registro.CorrelacionId, x.Registro.Origen, x.Registro.OcurridoEn,
            }),
            siguiente = b.Siguiente,
            series = b.Series.Select(x => new { contexto = x.Contexto, al_dia_hasta = x.AlDiaHasta }),
        });
    }
}
