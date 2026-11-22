// Casos de uso sobre dobles en memoria: jurisdicción (EC-01), auditoría de
// las denegaciones (Tabla 16), la salida de EV-07 con cada cambio, los
// indicadores y la bitácora compuesta (ADR-016) sobre las copias locales.

using RedVital.Institucional.Aplicacion;
using RedVital.Institucional.Aplicacion.CasosDeUso;
using RedVital.Institucional.Aplicacion.Puertos;
using RedVital.Institucional.Dominio;
using Xunit;

namespace RedVital.Institucional.Pruebas;

public class CasosDeUsoPruebas
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static ConsultaInstituciones Todas(string? ruta = null) => new(null, ruta, null, null, null, null, null);

    [Fact]
    public async Task El_coordinador_ve_las_instituciones_de_su_departamento()
    {
        var red = new Red();
        var r = await red.Consultar.EjecutarAsync(Fabrica.Ctx(Roles.Coordinador, "territorio:/00/05"), Todas(), Ct);
        Assert.True(r.Exitoso);
        Assert.Equal(["Banco Aurora", "Banco Ceiba"], r.Valor!.Elementos.Select(e => e.Nombre).Order());
    }

    [Fact]
    public async Task Fuera_de_la_jurisdiccion_es_403_y_queda_auditado()
    {
        var red = new Red();
        var ctx = Fabrica.Ctx(Roles.Coordinador, "territorio:/00/05");
        var r = await red.Consultar.EjecutarAsync(ctx, Todas("/00/11"), Ct);
        Assert.Equal(Fallo.FueraDeJurisdiccion, r.Fallo);
        var a = Assert.Single(red.Auditoria);
        Assert.Equal(("denegado", "consultar_instituciones", "territorio:/00/11", ctx.CorrelacionId),
            (a.Resultado, a.Operacion, a.JurisdiccionSolicitada, a.CorrelacionId));
    }

    [Fact]
    public async Task El_administrador_de_banco_solo_ve_la_suya()
    {
        var red = new Red();
        var ctx = Fabrica.Ctx(Roles.AdminBanco, $"institucion:{red.Aurora.Id}");
        var r = await red.Consultar.EjecutarAsync(ctx, Todas("/00/05"), Ct);
        Assert.Equal(red.Aurora.Id, Assert.Single(r.Valor!.Elementos).Id);
        Assert.Equal(Fallo.FueraDeJurisdiccion, (await red.Consultar.EjecutarAsync(ctx, Todas("/00/11"), Ct)).Fallo);
    }

    [Fact]
    public async Task El_operador_no_lista_instituciones_y_la_denegacion_se_audita()
    {
        var red = new Red();
        var r = await red.Consultar.EjecutarAsync(Fabrica.Ctx(Roles.Operador, $"institucion:{red.Aurora.Id}"), Todas(), Ct);
        Assert.Equal(Fallo.NoPermitido, r.Fallo);
        Assert.Equal("denegado", Assert.Single(red.Auditoria).Resultado);
    }

    [Fact]
    public async Task Una_baja_deja_de_figurar_salvo_que_se_pidan_las_bajas()
    {
        var red = new Red();
        red.Ceiba.Modificar(null, null, false);
        var coord = Fabrica.Ctx(Roles.Coordinador, "territorio:/00/05");
        Assert.Single((await red.Consultar.EjecutarAsync(coord, Todas(), Ct)).Valor!.Elementos);
        var conBajas = await red.Consultar.EjecutarAsync(coord, Todas() with { IncluirBajas = true }, Ct);
        Assert.Equal(2, conBajas.Valor!.Elementos.Count);
        // Al administrador de banco no se le muestran bajas aunque las pida.
        var admin = Fabrica.Ctx(Roles.AdminBanco, $"institucion:{red.Ceiba.Id}");
        Assert.Empty((await red.Consultar.EjecutarAsync(admin, Todas() with { IncluirBajas = true }, Ct)).Valor!.Elementos);
    }

    [Fact]
    public async Task El_coordinador_da_de_alta_dentro_de_su_jurisdiccion_y_no_fuera()
    {
        var red = new Red();
        var ctx = Fabrica.Ctx(Roles.Coordinador, "territorio:/00/05");
        var ok = await red.Crear.EjecutarAsync(ctx, new NuevaInstitucion("QA-0100", "Banco Nuevo", "banco_de_sangre", red.Bello.Id, null), Ct);
        Assert.True(ok.Exitoso, ok.Detalle);
        Assert.Equal("/00/05/05088", ok.Valor!.TerritorioRuta);
        Assert.Contains(red.Auditoria, a => a.Operacion == "crear_institucion" && a.Resultado == "permitido" && a.RecursoId == ok.Valor.Id);

        var fuera = await red.Crear.EjecutarAsync(ctx, new NuevaInstitucion("QA-0101", "Banco Lejano", "banco_de_sangre", red.Bogota.Id, null), Ct);
        Assert.Equal(Fallo.FueraDeJurisdiccion, fuera.Fallo);
        var repetida = await red.Crear.EjecutarAsync(ctx, new NuevaInstitucion("QA-0100", "Otro", "banco_de_sangre", red.Bello.Id, null), Ct);
        Assert.Equal(Fallo.Conflicto, repetida.Fallo);
        var enDepartamento = await red.Crear.EjecutarAsync(ctx, new NuevaInstitucion("QA-0102", "Otro", "banco_de_sangre", red.Antioquia.Id, null), Ct);
        Assert.Equal(Fallo.Invalido, enDepartamento.Fallo);
    }

    [Fact]
    public async Task Cada_alta_y_cada_cambio_dejan_su_EV07_con_el_estado_completo()
    {
        var red = new Red();
        var ctx = Fabrica.Ctx(Roles.Coordinador, "territorio:/00/05");
        var alta = await red.Crear.EjecutarAsync(ctx, new NuevaInstitucion("QA-0200", "Banco del Valle de Aburrá", "banco_de_sangre", red.Bello.Id, null), Ct);
        var (evento, correlacion) = Assert.Single(red.Bandeja);
        Assert.Equal((alta.Valor!.Id, "QA-0200", "Banco del Valle de Aburrá", "banco_de_sangre", "05088", "/00/05/05088", true),
            (evento.InstitucionId, evento.Codigo, evento.Nombre, evento.Tipo, evento.TerritorioCodigo, evento.TerritorioRuta, evento.Vigente));
        Assert.Equal(ctx.CorrelacionId, correlacion);

        await red.Modificar.EjecutarAsync(ctx, alta.Valor.Id, new CambioInstitucion("Banco Aburrá Norte", null, false, null), Ct);
        var cambio = red.Bandeja[^1].Evento;
        Assert.Equal(("Banco Aburrá Norte", false, "/00/05/05088"), (cambio.Nombre, cambio.Vigente, cambio.TerritorioRuta));
        Assert.True(cambio.CambiadoEn >= evento.CambiadoEn);

        // Lo que no se hace no sale: ni una denegación ni un cambio inválido.
        await red.Modificar.EjecutarAsync(Fabrica.Ctx(Roles.Coordinador, "territorio:/00/11"), alta.Valor.Id, new CambioInstitucion("X", null, null, null), Ct);
        await red.Modificar.EjecutarAsync(ctx, alta.Valor.Id, new CambioInstitucion(null, null, null, "servicio_transfusion"), Ct);
        Assert.Equal(2, red.Bandeja.Count);
    }

    [Fact]
    public async Task El_tipo_no_cambia_y_la_baja_se_audita_como_baja()
    {
        var red = new Red();
        var ctx = Fabrica.Ctx(Roles.AdminNacional, "territorio:/00");
        var tipo = await red.Modificar.EjecutarAsync(ctx, red.Aurora.Id, new CambioInstitucion(null, null, null, "servicio_transfusion"), Ct);
        Assert.Equal(Fallo.Conflicto, tipo.Fallo);
        var baja = await red.Modificar.EjecutarAsync(ctx, red.Aurora.Id, new CambioInstitucion(null, null, false, "banco_de_sangre"), Ct);
        Assert.False(baja.Valor!.Vigente);
        Assert.Contains(red.Auditoria, a => a.Operacion == "baja_institucion" && a.Resultado == "permitido");
    }

    [Fact]
    public async Task Modificar_fuera_de_la_jurisdiccion_responde_como_inexistente()
    {
        var red = new Red();
        var r = await red.Modificar.EjecutarAsync(Fabrica.Ctx(Roles.Coordinador, "territorio:/00/11"), red.Aurora.Id,
            new CambioInstitucion("Otro nombre", null, null, null), Ct);
        Assert.Equal(Fallo.NoEncontrado, r.Fallo);
        Assert.Equal("Banco Aurora", red.Aurora.Nombre);
        Assert.Equal("denegado", Assert.Single(red.Auditoria).Resultado);
    }

    [Fact]
    public async Task Solo_el_administrador_nacional_crea_territorios_y_la_ruta_la_calcula_el_servicio()
    {
        var red = new Red();
        var nacional = Fabrica.Ctx(Roles.AdminNacional, "territorio:/00");
        var r = await red.CrearTerritorio.EjecutarAsync(nacional, new NuevoTerritorio("05002", "Abejorral"), Ct);
        Assert.Equal(("/00/05/05002", red.Antioquia.Id), (r.Valor!.Ruta, r.Valor.PadreId));
        Assert.Equal(Fallo.Conflicto, (await red.CrearTerritorio.EjecutarAsync(nacional, new NuevoTerritorio("05002", "Otra vez"), Ct)).Fallo);
        Assert.Equal(Fallo.Invalido, (await red.CrearTerritorio.EjecutarAsync(nacional, new NuevoTerritorio("99999", "Sin departamento"), Ct)).Fallo);
        Assert.Equal(Fallo.NoPermitido, (await red.CrearTerritorio.EjecutarAsync(Fabrica.Ctx(Roles.Coordinador, "territorio:/00/05"), new NuevoTerritorio("05003", "X"), Ct)).Fallo);
    }

    [Fact]
    public async Task Los_territorios_se_acotan_a_la_jurisdiccion()
    {
        var red = new Red();
        var coord = await red.Territorios_.EjecutarAsync(Fabrica.Ctx(Roles.Coordinador, "territorio:/00/05"), new ConsultaTerritorios(null, null, null, null), Ct);
        Assert.Equal(["/00/05", "/00/05/05001", "/00/05/05088"], coord.Valor!.Elementos.Select(t => t.Ruta).Order());
        var admin = await red.Territorios_.EjecutarAsync(Fabrica.Ctx(Roles.AdminBanco, $"institucion:{red.Aurora.Id}"), new ConsultaTerritorios(null, null, null, null), Ct);
        Assert.Equal("/00/05/05001", Assert.Single(admin.Valor!.Elementos).Ruta);
        var auditor = await red.Territorios_.EjecutarAsync(Fabrica.Ctx(Roles.Auditor, "territorio:/00"), new ConsultaTerritorios("municipal", null, null, null), Ct);
        Assert.Equal(3, auditor.Valor!.Total);
    }

    // ------------------------------------------------------------ indicadores
    private static ConsultarIndicadores Indicadores(Red red, IndicadoresFijos indicadores) =>
        new(indicadores, red, red, red.Jurisdiccion, red.Denegaciones, TimeProvider.System);

    private static ConsultaIndicadores Periodo(Guid? inst = null, string? ruta = null) =>
        new(new DateOnly(2026, 8, 1), new DateOnly(2026, 10, 31), "mes", inst, ruta);

    [Fact]
    public async Task Los_indicadores_salen_de_la_copia_local_acotados_a_la_jurisdiccion()
    {
        var red = new Red();
        var indicadores = new IndicadoresFijos();
        var r = await Indicadores(red, indicadores).EjecutarAsync(Fabrica.Ctx(Roles.Coordinador, "territorio:/00/05"), Periodo(), Ct);
        Assert.True(r.Exitoso, r.Detalle);
        Assert.Equal(("/00/05", (Guid?)null), (indicadores.Recibido!.Alcance.Valor, indicadores.Recibido.InstitucionId));
        Assert.True(Assert.Single(r.Valor!.Captacion).SuprimidoPorAgregacion);
        Assert.Single(r.Valor.Cobertura);
        Assert.Equal(IndicadoresFijos.Fotografia, r.Valor.ActualizadoEn);

        var admin = Fabrica.Ctx(Roles.AdminBanco, $"institucion:{red.Aurora.Id}");
        Assert.True((await Indicadores(red, indicadores).EjecutarAsync(admin, Periodo(), Ct)).Exitoso);
        Assert.Equal(red.Aurora.Id, indicadores.Recibido!.InstitucionId);
        Assert.Equal(Fallo.FueraDeJurisdiccion, (await Indicadores(red, indicadores).EjecutarAsync(admin, Periodo(red.Ceiba.Id), Ct)).Fallo);
        Assert.Equal(Fallo.NoPermitido, (await Indicadores(red, indicadores).EjecutarAsync(Fabrica.Ctx(Roles.Auditor, "territorio:/00"), Periodo(), Ct)).Fallo);
    }

    [Fact]
    public async Task Los_indicadores_validan_el_periodo()
    {
        var red = new Red();
        var ctx = Fabrica.Ctx(Roles.AdminNacional, "territorio:/00");
        var c = Indicadores(red, new IndicadoresFijos());
        Assert.Equal(Fallo.Invalido, (await c.EjecutarAsync(ctx, Periodo() with { AgruparPor = "anio" }, Ct)).Fallo);
        Assert.Equal(Fallo.Invalido, (await c.EjecutarAsync(ctx, Periodo() with { Desde = new DateOnly(2026, 12, 1) }, Ct)).Fallo);
        Assert.Equal(Fallo.Invalido, (await c.EjecutarAsync(ctx, Periodo() with { Desde = new DateOnly(2020, 1, 1) }, Ct)).Fallo);
    }

    // --------------------------------------------------------------- bitácora
    private static RegistroAuditoria Hecho(DateTimeOffset t, string operacion, string correlacion = "c-1") =>
        new(Guid.NewGuid(), "usuario", "u", "operador", null, operacion, "unidad", null, "permitido", correlacion, null, t);

    [Fact]
    public async Task La_bitacora_funde_las_cuatro_series_por_fecha_y_pagina_con_cursor()
    {
        var t0 = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var red = new Red();
        var fuentes = new[]
        {
            new FuenteFija("identidad", Hecho(t0.AddMinutes(1), "iniciar_sesion"), Hecho(t0.AddMinutes(5), "cerrar_sesion")),
            new FuenteFija("campanias", Hecho(t0.AddMinutes(3), "publicar_campania")),
            new FuenteFija("donacion", Hecho(t0.AddMinutes(2), "registrar_donacion"), Hecho(t0.AddMinutes(4), "tamizar_unidad")),
            new FuenteFija("institucional"),
        };
        var bitacora = new ConsultarBitacora(fuentes, red.Denegaciones);
        var ctx = Fabrica.Ctx(Roles.Auditor, "territorio:/00");

        var p1 = await bitacora.EjecutarAsync(ctx, new ConsultaBitacora(null, null, null, null, null, null, 3, null), Ct);
        Assert.Equal(["cerrar_sesion", "tamizar_unidad", "publicar_campania"], p1.Valor!.Registros.Select(x => x.Registro.Operacion));
        Assert.Equal(["identidad", "donacion", "campanias"], p1.Valor.Registros.Select(x => x.Contexto));
        Assert.NotNull(p1.Valor.Siguiente);
        Assert.Equal(4, fuentes[0].Recibido!.Tamano); // uno de más para saber si hay otra página

        var p2 = await bitacora.EjecutarAsync(ctx, new ConsultaBitacora(null, null, null, null, null, null, 3, p1.Valor.Siguiente), Ct);
        Assert.Equal(["registrar_donacion", "iniciar_sesion"], p2.Valor!.Registros.Select(x => x.Registro.Operacion));
        Assert.Null(p2.Valor.Siguiente);
        // Cada consulta de la bitácora queda auditada (Tabla 16).
        Assert.Equal(2, red.Auditoria.Count(a => a.Operacion == "consultar_bitacora" && a.Resultado == "permitido"));
    }

    [Fact]
    public async Task La_bitacora_dice_hasta_cuando_esta_al_dia_cada_serie()
    {
        var red = new Red();
        var hace = DateTimeOffset.UtcNow.AddSeconds(-30);
        var fuentes = new[]
        {
            new FuenteFija("identidad", Hecho(DateTimeOffset.UtcNow, "iniciar_sesion")) { AlDiaHasta = hace },
            new FuenteFija("donacion"),
            new FuenteFija("campanias") { AlDiaHasta = hace.AddMinutes(-2) },
        };
        var r = await new ConsultarBitacora(fuentes, red.Denegaciones)
            .EjecutarAsync(Fabrica.Ctx(Roles.Auditor, "territorio:/00"), new ConsultaBitacora(null, null, null, null, null, null, null, null), Ct);
        Assert.Equal(["campanias", "donacion", "identidad"], r.Valor!.Series.Select(x => x.Contexto));
        Assert.Equal([hace.AddMinutes(-2), null, hace], r.Valor.Series.Select(x => x.AlDiaHasta));
        Assert.Single(r.Valor.Registros);
    }

    [Fact]
    public async Task Solo_el_auditor_consulta_la_bitacora_y_los_filtros_se_validan()
    {
        var red = new Red();
        var b = new ConsultarBitacora([new FuenteFija("identidad")], red.Denegaciones);
        var vacia = new ConsultaBitacora(null, null, null, null, null, null, null, null);
        Assert.Equal(Fallo.NoPermitido, (await b.EjecutarAsync(Fabrica.Ctx(Roles.AdminNacional, "territorio:/00"), vacia, Ct)).Fallo);
        Assert.Contains(red.Auditoria, a => a.Operacion == "consultar_bitacora" && a.Resultado == "denegado");
        var auditor = Fabrica.Ctx(Roles.Auditor, "territorio:/00");
        Assert.Equal(Fallo.Invalido, (await b.EjecutarAsync(auditor, vacia with { Tamano = 500 }, Ct)).Fallo);
        Assert.Equal(Fallo.Invalido, (await b.EjecutarAsync(auditor, vacia with { AntesDe = "no-es-un-cursor" }, Ct)).Fallo);
    }
}
