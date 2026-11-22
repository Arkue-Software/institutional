// Reglas del dominio: la ruta territorial (IN-13), la jerarquía, la
// institución (EC-39, EC-42) y la matriz de acceso (DD 15.5, Tabla 37).

using RedVital.Institucional.Dominio;
using Xunit;

namespace RedVital.Institucional.Pruebas;

public class DominioPruebas
{
    [Theory]
    [InlineData("00", "/00", NivelTerritorial.Nacional)]
    [InlineData("05", "/00/05", NivelTerritorial.Departamental)]
    [InlineData("05001", "/00/05/05001", NivelTerritorial.Municipal)]
    public void La_ruta_se_deriva_del_codigo(string codigo, string ruta, NivelTerritorial nivel)
    {
        var r = RutaTerritorial.DeCodigo(codigo);
        Assert.Equal(ruta, r.Valor);
        Assert.Equal(nivel, r.Nivel);
        Assert.Equal(codigo, r.CodigoDivipola);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("0500")]
    [InlineData("ab")]
    [InlineData("050011")]
    public void Un_codigo_invalido_no_tiene_ruta(string codigo) =>
        Assert.Throws<ReglaDominio>(() => RutaTerritorial.DeCodigo(codigo));

    [Theory]
    [InlineData("/00", true)]
    [InlineData("/00/05/05001", true)]
    [InlineData("/00/05/11001", false)]   // el municipio no es de ese departamento
    [InlineData("/05/05001", false)]
    [InlineData("/00/5", false)]
    [InlineData("/00/05/", false)]
    [InlineData("", false)]
    public void Solo_se_aceptan_las_rutas_que_produce_la_regla(string texto, bool valida) =>
        Assert.Equal(valida, RutaTerritorial.TryParse(texto, out _));

    [Theory]
    [InlineData("/00", "/00/05/05001", true)]
    [InlineData("/00/05", "/00/05", true)]
    [InlineData("/00/05", "/00/05/05088", true)]
    [InlineData("/00/05", "/00/08/08001", false)]
    [InlineData("/00/05/05001", "/00/05", false)]
    public void Contener_es_una_comparacion_de_prefijo_por_segmentos(string base_, string otra, bool contiene)
    {
        RutaTerritorial.TryParse(base_, out var b);
        RutaTerritorial.TryParse(otra, out var o);
        Assert.Equal(contiene, b.Contiene(o));
    }

    private static Territorio Arbol(out Territorio antioquia)
    {
        var nacion = Fabrica.Territorio("00", "Colombia", NivelTerritorial.Nacional, null);
        antioquia = Fabrica.Territorio("05", "Antioquia", NivelTerritorial.Departamental, nacion.Id);
        return nacion;
    }

    [Fact]
    public void Un_municipio_cuelga_de_su_departamento_y_calcula_su_ruta()
    {
        Arbol(out var antioquia);
        var m = Territorio.Crear("05002", " Abejorral ", antioquia);
        Assert.Equal("/00/05/05002", m.Ruta);
        Assert.Equal(NivelTerritorial.Municipal, m.Nivel);
        Assert.Equal(antioquia.Id, m.PadreId);
        Assert.Equal("Abejorral", m.Nombre);
    }

    [Fact]
    public void Un_municipio_no_cuelga_de_otro_departamento_ni_de_la_nacion()
    {
        var nacion = Arbol(out var antioquia);
        Assert.Throws<ReglaDominio>(() => Territorio.Crear("11001", "Bogotá", antioquia));
        Assert.Throws<ReglaDominio>(() => Territorio.Crear("05002", "Abejorral", nacion));
        Assert.Throws<ReglaDominio>(() => Territorio.Crear("00", "Otra raíz", nacion));
        Assert.Throws<ReglaDominio>(() => Territorio.Crear("05002", "  ", antioquia));
    }

    [Fact]
    public void Una_institucion_nace_vigente_en_un_municipio_y_su_tipo_no_cambia()
    {
        Arbol(out var antioquia);
        var medellin = Territorio.Crear("05001", "Medellín", antioquia);
        var autor = Guid.NewGuid();
        var ahora = DateTimeOffset.UtcNow;
        var i = Institucion.Crear("QA-1234", "Banco ficticio", TipoInstitucion.BancoDeSangre, medellin, " ", autor, ahora);
        Assert.True(i.Vigente);
        Assert.Null(i.Direccion);
        Assert.Equal(ahora, i.CreadoEn);

        i.Modificar(null, "Calle 1", false);
        Assert.False(i.Vigente);
        Assert.Equal("Calle 1", i.Direccion);
        Assert.Equal(TipoInstitucion.BancoDeSangre, i.Tipo);
        Assert.Throws<ReglaDominio>(() => Institucion.Crear("QA-1234", "Banco", TipoInstitucion.BancoDeSangre, antioquia, null, autor, ahora));
        Assert.Throws<ReglaDominio>(() => Institucion.Crear("Q", "Banco", TipoInstitucion.BancoDeSangre, medellin, null, autor, ahora));
    }

    [Fact]
    public void La_jurisdiccion_se_lee_del_token()
    {
        var inst = Guid.NewGuid();
        Assert.Equal(inst, new Solicitante("x", Roles.AdminBanco, $"institucion:{inst}").InstitucionJurisdiccion);
        Assert.Equal("/00/05", new Solicitante("x", Roles.Coordinador, "territorio:/00/05").RutaJurisdiccion!.Valor);
        Assert.Null(new Solicitante("x", Roles.Coordinador, "territorio:/00/5").RutaJurisdiccion);
        Assert.Null(new Solicitante("x", Roles.AdminBanco, "institucion:no-es-uuid").InstitucionJurisdiccion);
    }

    // Matriz normativa: grupo 10 (L/E), 12 y 13, más la resolución por id.
    [Theory]
    //            rol               lee-terr crea-terr lee-inst escribe-inst indicadores bitacora resolver
    [InlineData(Roles.Donante,       false, false, false, false, false, false, true)]
    [InlineData(Roles.Operador,      false, false, false, false, false, false, true)]
    [InlineData(Roles.AdminBanco,    true,  false, true,  false, true,  false, true)]
    [InlineData(Roles.Coordinador,   true,  false, true,  true,  true,  false, true)]
    [InlineData(Roles.AdminNacional, true,  true,  true,  true,  true,  false, true)]
    [InlineData(Roles.Auditor,       true,  false, true,  false, false, true,  true)]
    public void La_matriz_de_acceso_coincide_con_la_Tabla_37(string rol, bool leeTerr, bool creaTerr, bool leeInst,
        bool escribeInst, bool indicadores, bool bitacora, bool resolver)
    {
        var s = new Solicitante(Guid.NewGuid().ToString(), rol, null);
        Assert.Equal(leeTerr, PoliticaAcceso.LeeTerritorios(s));
        Assert.Equal(creaTerr, PoliticaAcceso.CreaTerritorios(s));
        Assert.Equal(leeInst, PoliticaAcceso.LeeInstituciones(s));
        Assert.Equal(escribeInst, PoliticaAcceso.EscribeInstituciones(s));
        Assert.Equal(indicadores, PoliticaAcceso.LeeIndicadores(s));
        Assert.Equal(bitacora, PoliticaAcceso.LeeBitacora(s));
        Assert.Equal(resolver, PoliticaAcceso.ResuelveInstitucion(s));
    }

    [Fact]
    public void Ningun_servicio_lista_instituciones_porque_entre_servicios_todo_va_por_Kafka()
    {
        Assert.False(PoliticaAcceso.LeeInstituciones(new Solicitante("donacion", Roles.Servicio, null)));
        Assert.False(PoliticaAcceso.LeeInstituciones(new Solicitante("campanias", Roles.Servicio, null)));
        Assert.False(PoliticaAcceso.ResuelveInstitucion(Solicitante.Anonimo));
    }
}
