// Adaptadores de persistencia contra PostgreSQL real: territorios con la
// geografía de V001, instituciones con los permisos por columna, cobertura, la
// serie de auditoría con su paginación por cursor, la bandeja de EV-07 y las
// copias de EV-09 y EV-10.

using Npgsql;
using RedVital.Institucional.Api.Mantenimiento;
using RedVital.Institucional.Aplicacion;
using RedVital.Institucional.Aplicacion.Puertos;
using RedVital.Institucional.Dominio;
using RedVital.Institucional.Infraestructura.Persistencia;
using Xunit;

namespace RedVital.Institucional.Pruebas;

[Collection(ColeccionBase.Nombre)]
public class PostgresPruebas(BaseInstitucional db)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static RutaTerritorial Ruta(string r)
    {
        Assert.True(RutaTerritorial.TryParse(r, out var ruta));
        return ruta;
    }

    [Fact]
    public async Task La_geografia_de_V001_esta_cargada_y_se_filtra_por_prefijo()
    {
        await using var c = db.Contexto();
        var repo = new RepositorioTerritorios(c);
        var antioquia = await repo.ListarAsync(new FiltroTerritorios(Ruta("/00/05"), NivelTerritorial.Municipal, null, 1, 500), Ct);
        Assert.Contains(antioquia.Elementos, t => t.CodigoDivipola == "05001" && t.Nombre == "Medellín");
        Assert.All(antioquia.Elementos, t => Assert.StartsWith("/00/05/", t.Ruta));
        var departamentos = await repo.ListarAsync(new FiltroTerritorios(Ruta("/00"), NivelTerritorial.Departamental, null, 1, 500), Ct);
        Assert.Equal(33, departamentos.Total);
    }

    [Fact]
    public async Task Alta_de_territorio_y_codigo_repetido()
    {
        await using var c = db.Contexto();
        var repo = new RepositorioTerritorios(c);
        var antioquia = (await repo.PorCodigoAsync("05", Ct))!;
        var nuevo = Territorio.Crear("05004", "Abriaquí", antioquia);
        await repo.AgregarAsync(nuevo, Ct);
        Assert.True((await repo.PorIdAsync(nuevo.Id, Ct))!.Vigente); // lo pone la base

        await using var otro = db.Contexto();
        await Assert.ThrowsAsync<Duplicado>(() => new RepositorioTerritorios(otro).AgregarAsync(Territorio.Crear("05004", "Otra", antioquia), Ct));
    }

    [Fact]
    public async Task Una_institucion_se_crea_lista_modifica_y_da_de_baja_con_los_permisos_del_servicio()
    {
        await using var c = db.Contexto();
        var territorios = new RepositorioTerritorios(c);
        var repo = new RepositorioInstituciones(c);
        var envigado = (await territorios.PorCodigoAsync("05266", Ct))!;
        var nueva = Institucion.Crear($"PG-{Random.Shared.Next(100000, 999999)}", "Banco Postgres (ficticio)", TipoInstitucion.BancoDeSangre,
            envigado, "Calle 1", Guid.NewGuid(), DateTimeOffset.UtcNow.AddYears(-3));
        await repo.AgregarAsync(nueva, Ct);

        var vista = (await repo.VistaAsync(nueva.Id, Ct))!;
        Assert.Equal(("banco_de_sangre", "05266", "/00/05/05266", true), (vista.Tipo, vista.TerritorioCodigo, vista.TerritorioRuta, vista.Vigente));
        // EC-42: el momento del alta lo fija la base, no el que mandó la aplicación.
        Assert.True(vista.CreadoEn > DateTimeOffset.UtcNow.AddMinutes(-5));

        var antioquia = await repo.ListarAsync(new FiltroInstituciones(Ruta("/00/05"), true, null, null, false, 1, 100), Ct);
        Assert.Contains(antioquia.Elementos, v => v.Id == nueva.Id);
        var soloDepartamento = await repo.ListarAsync(new FiltroInstituciones(Ruta("/00/05"), false, null, null, false, 1, 100), Ct);
        Assert.DoesNotContain(soloDepartamento.Elementos, v => v.Id == nueva.Id);

        var entidad = (await repo.PorIdAsync(nueva.Id, Ct))!;
        entidad.Modificar("Banco Postgres renombrado (ficticio)", null, false);
        await repo.GuardarAsync(entidad, Ct);
        var vigentes = await repo.ListarAsync(new FiltroInstituciones(Ruta("/00/05/05266"), true, null, null, false, 1, 100), Ct);
        Assert.DoesNotContain(vigentes.Elementos, v => v.Id == nueva.Id);
        var conBajas = await repo.ListarAsync(new FiltroInstituciones(Ruta("/00/05/05266"), true, null, null, true, 1, 100), Ct);
        Assert.Equal("Banco Postgres renombrado (ficticio)", conBajas.Elementos.Single(v => v.Id == nueva.Id).Nombre);
    }

    [Fact]
    public async Task Un_codigo_de_habilitacion_repetido_es_duplicado()
    {
        var codigo = $"PG-{Random.Shared.Next(100000, 999999)}";
        await using var c = db.Contexto();
        var bello = (await new RepositorioTerritorios(c).PorCodigoAsync("05088", Ct))!;
        await new RepositorioInstituciones(c).AgregarAsync(Institucion.Crear(codigo, "Uno", TipoInstitucion.BancoDeSangre, bello, null, Guid.NewGuid(), DateTimeOffset.UtcNow), Ct);
        await using var otro = db.Contexto();
        await Assert.ThrowsAsync<Duplicado>(() => new RepositorioInstituciones(otro)
            .AgregarAsync(Institucion.Crear(codigo, "Dos", TipoInstitucion.ServicioTransfusion, bello, null, Guid.NewGuid(), DateTimeOffset.UtcNow), Ct));
    }

    [Fact]
    public async Task La_cobertura_cuenta_municipios_con_banco_por_departamento()
    {
        await using var c = db.Contexto();
        var t = new RepositorioTerritorios(c);
        var repo = new RepositorioInstituciones(c);
        var leticia = (await t.PorCodigoAsync("91001", Ct))!;
        await repo.AgregarAsync(Institucion.Crear($"PG-{Random.Shared.Next(100000, 999999)}", "Banco Selva (ficticio)", TipoInstitucion.BancoDeSangre, leticia, null, Guid.NewGuid(), DateTimeOffset.UtcNow), Ct);
        await repo.AgregarAsync(Institucion.Crear($"PG-{Random.Shared.Next(100000, 999999)}", "Servicio Río (ficticio)", TipoInstitucion.ServicioTransfusion, leticia, null, Guid.NewGuid(), DateTimeOffset.UtcNow), Ct);

        var amazonas = Assert.Single(await new ConsultaCobertura(c).DentroDeAsync(Ruta("/00/91"), Ct));
        Assert.Equal(("91", 1, 1, 1, 1), (amazonas.TerritorioCodigo, amazonas.Municipios, amazonas.MunicipiosConBanco, amazonas.BancosDeSangre, amazonas.ServiciosTransfusion));
        var nacional = await new ConsultaCobertura(c).DentroDeAsync(Ruta("/00"), Ct);
        Assert.Equal(33, nacional.Count);
        // Desde un municipio, la cobertura es la de su departamento acotada a él.
        var municipio = Assert.Single(await new ConsultaCobertura(c).DentroDeAsync(Ruta("/00/91/91001"), Ct));
        Assert.Equal(("91", 1), (municipio.TerritorioCodigo, municipio.Municipios));
    }

    [Fact]
    public async Task La_serie_propia_filtra_y_pagina_por_cursor()
    {
        var correlacion = Guid.NewGuid().ToString();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        await using (var c = db.Contexto())
        {
            var registro = new RegistroAuditoriaInst(c);
            for (var i = 0; i < 5; i++)
                await registro.RegistrarAsync(new RegistroAuditoria(Guid.NewGuid(), "usuario", "u", Roles.AdminNacional, null,
                    "crear_institucion", "institucion", null, "permitido", correlacion, "10.0.0.1", t0.AddSeconds(i)), Ct);
        }

        await using var lectura = db.Contexto();
        var fuente = new FuenteAuditoriaPropia(lectura, TimeProvider.System);
        var p1 = await fuente.ConsultarAsync(new FiltroBitacora(null, null, correlacion, null, null, null, 3, null, null), Ct);
        Assert.Equal(3, p1.Count);
        Assert.True(p1[0].OcurridoEn > p1[1].OcurridoEn);
        var ultimo = p1[^1];
        var p2 = await fuente.ConsultarAsync(new FiltroBitacora(null, null, correlacion, null, null, null, 3, ultimo.OcurridoEn, ultimo.Id), Ct);
        Assert.Equal(2, p2.Count);
        Assert.Empty(p1.Select(r => r.Id).Intersect(p2.Select(r => r.Id)));
        var porTipo = await fuente.ConsultarAsync(new FiltroBitacora("territorio", null, correlacion, null, null, null, 10, null, null), Ct);
        Assert.Empty(porTipo);
        var desde = await fuente.ConsultarAsync(new FiltroBitacora(null, null, correlacion, null, t0.AddSeconds(3), null, 10, null, null), Ct);
        Assert.Equal(2, desde.Count);
    }

    private async Task<Institucion> InstitucionEnAsync(string municipio)
    {
        await using var c = db.Contexto();
        var t = (await new RepositorioTerritorios(c).PorCodigoAsync(municipio, Ct))!;
        var i = Institucion.Crear($"PG-{Random.Shared.Next(100000, 999999)}", "Banco Indicadores (ficticio)", TipoInstitucion.BancoDeSangre,
            t, null, Guid.NewGuid(), DateTimeOffset.UtcNow);
        await new RepositorioInstituciones(c).AgregarAsync(i, Ct);
        return i;
    }

    [Fact]
    public async Task El_EV07_se_guarda_con_el_cambio_de_la_institucion_en_la_misma_transaccion()
    {
        await using var c = db.Contexto();
        var bello = (await new RepositorioTerritorios(c).PorCodigoAsync("05088", Ct))!;
        var i = Institucion.Crear($"PG-{Random.Shared.Next(100000, 999999)}", "Banco Bandeja (ficticio)", TipoInstitucion.BancoDeSangre,
            bello, null, Guid.NewGuid(), DateTimeOffset.UtcNow);
        new BandejaSalida(c).Encolar(new InstitucionCambiada(i.Id, i.Codigo, i.Nombre, "banco_de_sangre", "05088", "/00/05/05088", true,
            DateTimeOffset.UtcNow), "corr-bandeja");
        await new RepositorioInstituciones(c).AgregarAsync(i, Ct);

        await using var lectura = db.Contexto();
        var e = lectura.EventosSalida.Single(x => x.Clave == i.Id.ToString());
        Assert.Equal(("institution.changed", "redvital.institucional.institution-changed.v1", "pendiente", "corr-bandeja"),
            (e.Tipo, e.Tema, e.Estado, e.CorrelacionId));
        Assert.True(e.Secuencia > 0);
        using var carga = System.Text.Json.JsonDocument.Parse(e.Carga);
        Assert.Equal("/00/05/05088", carga.RootElement.GetProperty("territorio_ruta").GetString());
        Assert.Equal(i.Id, carga.RootElement.GetProperty("institucion_id").GetGuid());

        // Si el cambio no se guarda (código repetido), su evento tampoco.
        await using var otro = db.Contexto();
        var copia = Institucion.Crear(i.Codigo, "Otra", TipoInstitucion.BancoDeSangre, bello, null, Guid.NewGuid(), DateTimeOffset.UtcNow);
        new BandejaSalida(otro).Encolar(new InstitucionCambiada(copia.Id, copia.Codigo, "Otra", "banco_de_sangre", "05088", "/00/05/05088", true,
            DateTimeOffset.UtcNow), "corr-repetida");
        await Assert.ThrowsAsync<Duplicado>(() => new RepositorioInstituciones(otro).AgregarAsync(copia, Ct));
        await using var verificacion = db.Contexto();
        Assert.False(verificacion.EventosSalida.Any(x => x.Clave == copia.Id.ToString()));
    }

    [Fact]
    public async Task La_fotografia_de_indicadores_reemplaza_la_anterior_y_se_agrega_con_supresion()
    {
        var aurora = await InstitucionEnAsync("05001");
        var ceiba = await InstitucionEnAsync("05088");
        var t = DateTimeOffset.UtcNow.AddMinutes(-5);
        var dia = new DateOnly(2026, 10, 5);
        await using (var c = db.Contexto())
        {
            var p = new IndicadoresProyectados(c);
            Assert.True(await p.ReemplazarAsync(Guid.NewGuid(), new FotografiaIndicadores(aurora.Id, t,
                [new DiaCaptacion(dia, 9, 27, 99, 0)], [new ExistenciaRecibida("plasma", "O+", 1, 1, true)]), Ct));
            // La nueva reemplaza todo lo de esa institución.
            var nueva = Guid.NewGuid();
            Assert.True(await p.ReemplazarAsync(nueva, new FotografiaIndicadores(aurora.Id, t.AddMinutes(1),
                [new DiaCaptacion(dia, 4, 12, 1, 0), new DiaCaptacion(dia.AddDays(1), 2, 6, 0, 1)],
                [new ExistenciaRecibida("globulos_rojos", "O+", 10, 2, false)]), Ct));
            Assert.False(await p.ReemplazarAsync(nueva, new FotografiaIndicadores(aurora.Id, t.AddMinutes(1), [], []), Ct)); // duplicado
            // Una más vieja que llega tarde no cambia nada.
            Assert.True(await p.ReemplazarAsync(Guid.NewGuid(), new FotografiaIndicadores(aurora.Id, t.AddMinutes(-10), [], []), Ct));
            Assert.True(await p.ReemplazarAsync(Guid.NewGuid(), new FotografiaIndicadores(ceiba.Id, t,
                [new DiaCaptacion(dia, 3, 9, 1, 0)], [new ExistenciaRecibida("globulos_rojos", "A-", 0, 0, true)]), Ct));
        }

        await using var lectura = db.Contexto();
        var consulta = new IndicadoresProyectados(lectura);
        var soloAurora = new FiltroIndicadores(dia.AddDays(-1), dia.AddDays(5), "dia", aurora.Id, Ruta("/00/05/05001"));
        var dias = await consulta.CaptacionAsync(soloAurora, Ct);
        Assert.Equal(["2026-10-05", "2026-10-06"], dias.Select(x => x.Periodo));
        Assert.Equal((4L, (long?)null, true), (dias[0].Donaciones, dias[0].UnidadesNoAptas, dias[0].SuprimidoPorAgregacion));
        Assert.Equal("globulos_rojos", Assert.Single(await consulta.InventarioAsync(soloAurora, Ct)).Componente);
        Assert.Equal(t.AddMinutes(1).ToUnixTimeMilliseconds(), (await consulta.ActualizadoEnAsync(soloAurora, Ct))!.Value.ToUnixTimeMilliseconds());

        // El departamento suma las dos: 4 + 2 + 3 = 9 en el mes, y con 5 o más se muestran las no aptas.
        var antioquia = new FiltroIndicadores(dia.AddDays(-1), dia.AddDays(5), "mes", null, Ruta("/00/05"));
        var mes = Assert.Single(await consulta.CaptacionAsync(antioquia, Ct), x => x.Periodo == "2026-10");
        Assert.True(mes.Donaciones >= 9);
        Assert.False(mes.SuprimidoPorAgregacion);
        Assert.NotNull(mes.UnidadesNoAptas);
        var semanas = await consulta.CaptacionAsync(antioquia with { AgruparPor = "semana" }, Ct);
        Assert.Contains(semanas, x => x.Periodo == "2026-W41");
        Assert.Contains(await consulta.InventarioAsync(antioquia, Ct), x => x.InstitucionId == ceiba.Id && x.BajoUmbral);
    }

    [Fact]
    public async Task La_bitacora_recibida_no_duplica_y_cada_serie_se_lee_por_separado()
    {
        var correlacion = Guid.NewGuid().ToString();
        var registro = new RegistroAuditoria(Guid.NewGuid(), "usuario", "u5", Roles.Operador, null, "registrar_donacion", "donacion",
            Guid.NewGuid(), "permitido", correlacion, "10.0.0.9", DateTimeOffset.UtcNow.AddSeconds(-3));
        await using (var c = db.Contexto())
        {
            var b = new BitacoraRecibida(c);
            var evento = Guid.NewGuid();
            Assert.True(await b.AnexarAsync(evento, "donacion", registro, Ct));
            Assert.False(await b.AnexarAsync(evento, "donacion", registro, Ct));
            // El mismo registro con otro evento (un reenvío del productor) tampoco se duplica.
            Assert.True(await b.AnexarAsync(Guid.NewGuid(), "donacion", registro, Ct));
            Assert.True(await b.AnexarAsync(Guid.NewGuid(), "identidad", registro with { Id = Guid.NewGuid(), Operacion = "iniciar_sesion" }, Ct));
        }

        await using var lectura = db.Contexto();
        var filtro = new FiltroBitacora(null, null, correlacion, null, null, null, 10, null, null);
        var donacion = new FuenteAuditoriaRecibida("donacion", lectura);
        var leido = Assert.Single(await donacion.ConsultarAsync(filtro, Ct));
        Assert.Equal(registro with { OcurridoEn = leido.OcurridoEn }, leido);
        Assert.True((leido.OcurridoEn - registro.OcurridoEn).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.Equal("iniciar_sesion", Assert.Single(await new FuenteAuditoriaRecibida("identidad", lectura).ConsultarAsync(filtro, Ct)).Operacion);
        Assert.Empty(await new FuenteAuditoriaRecibida("campanias", lectura).ConsultarAsync(filtro, Ct));
        Assert.True(await donacion.AlDiaHastaAsync(Ct) > DateTimeOffset.UtcNow.AddMinutes(-1));
        var e = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.EscalarAsync<int>("DELETE FROM registro_bitacora"));
        Assert.Equal("42501", e.SqlState);
    }

    [Fact]
    public async Task La_depuracion_borra_solo_lo_publicado_o_procesado_que_ya_vencio()
    {
        var vencido = Guid.NewGuid();
        var vigente = Guid.NewGuid();
        var pendiente = Guid.NewGuid();
        var procesadoVencido = Guid.NewGuid();
        var procesadoVigente = Guid.NewGuid();
        await using (var conexion = new NpgsqlConnection(db.CadenaServicio))
        {
            await conexion.OpenAsync();
            var sql = $$"""
                INSERT INTO evento_salida (id, tipo, version_esquema, tema, clave, carga, correlacion_id, estado, publicado_en, expira_en)
                VALUES ('{{vencido}}', 'institution.changed', 1, 't', 'c', '{}', 'c', 'publicado', now() - interval '10 days', now() - interval '1 day'),
                       ('{{vigente}}', 'institution.changed', 1, 't', 'c', '{}', 'c', 'publicado', now(), now() + interval '6 days'),
                       ('{{pendiente}}', 'institution.changed', 1, 't', 'c', '{}', 'c', 'pendiente', NULL, NULL);
                INSERT INTO evento_procesado (grupo_consumidor, evento_id, tipo, procesado_en, expira_en)
                VALUES ('g', '{{procesadoVencido}}', 't', now() - interval '20 days', now() - interval '1 day'),
                       ('g', '{{procesadoVigente}}', 't', now(), now() + interval '13 days');
                """;
            await new NpgsqlCommand(sql, conexion).ExecuteNonQueryAsync();
        }

        await using (var c = db.Contexto())
        {
            Assert.True(await DepuradorRegistrosTecnicos.DepurarAsync(c, CancellationToken.None) >= 2);
        }

        await using var verificacion = new NpgsqlConnection(db.CadenaServicio);
        await verificacion.OpenAsync();
        async Task<bool> Existe(string tabla, string columna, Guid id) =>
            (long)(await new NpgsqlCommand($"SELECT count(*) FROM {tabla} WHERE {columna} = '{id}'", verificacion).ExecuteScalarAsync())! == 1;
        Assert.False(await Existe("evento_salida", "id", vencido));
        Assert.True(await Existe("evento_salida", "id", vigente));
        Assert.True(await Existe("evento_salida", "id", pendiente));
        Assert.False(await Existe("evento_procesado", "evento_id", procesadoVencido));
        Assert.True(await Existe("evento_procesado", "evento_id", procesadoVigente));

        // Nada de esta prueba queda pendiente: un publicador de otra prueba lo intentaría enviar.
        await new NpgsqlCommand($"DELETE FROM evento_salida WHERE id IN ('{vigente}', '{pendiente}')", verificacion).ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task El_servicio_no_puede_reescribir_la_bitacora()
    {
        var e = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.EscalarAsync<int>("UPDATE registro_auditoria_inst SET resultado='denegado'"));
        Assert.Equal("42501", e.SqlState); // permiso denegado
    }
}
