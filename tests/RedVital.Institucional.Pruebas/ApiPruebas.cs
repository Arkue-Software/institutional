// El servicio completo en memoria (WebApplicationFactory) sobre PostgreSQL
// real: validación del token con la llave pública montada (EC-03), forma de
// los problemas RFC 9457, propiedades desconocidas, roles, la salida de EV-07
// y la bitácora y los indicadores sobre las copias que llegan por Kafka.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using RedVital.Institucional.Aplicacion.Puertos;
using RedVital.Institucional.Dominio;
using RedVital.Institucional.Infraestructura.Persistencia;
using Xunit;

namespace RedVital.Institucional.Pruebas;

[Collection(ColeccionBase.Nombre)]
public class ApiPruebas : IDisposable
{
    private static readonly RSA Llave = RSA.Create(2048);
    private static readonly string Aurora = "a1000000-0000-4000-8000-000000000001";
    private readonly WebApplicationFactory<Program> _fabrica;
    private readonly BaseInstitucional _db;

    public ApiPruebas(BaseInstitucional db)
    {
        _db = db;
        var secretos = Directory.CreateTempSubdirectory("secretos-").FullName;
        var llavePublica = Path.Combine(Directory.CreateTempSubdirectory("llave-").FullName, "identidad_llave_publica");
        File.WriteAllText(llavePublica, Llave.ExportSubjectPublicKeyInfoPem());
        _fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Pruebas");
            b.UseSetting("ConnectionStrings:Institucional", db.CadenaServicio);
            // Un directorio vacío: en /tmp hay pipes del proceso de pruebas, y leerlos como secretos bloquea.
            b.UseSetting("Secretos:Directorio", secretos);
            b.UseSetting("Jwt:Emisor", "https://identidad.redvital.local");
            b.UseSetting("Jwt:Audiencia", "redvital");
            b.UseSetting("Jwt:LlavePublica", llavePublica);
        });
        // La institución de las cuentas de prueba, como la semilla de QA.
        using var c = db.Contexto();
        if (!c.Instituciones.Any(i => i.Id == Guid.Parse(Aurora)))
        {
            var medellin = c.Territorios.Single(t => t.CodigoDivipola == "05001");
            var i = Institucion.Crear("API-0001", "Banco Aurora (ficticio)", TipoInstitucion.BancoDeSangre, medellin, null, Guid.NewGuid(), DateTimeOffset.UtcNow);
            typeof(Institucion).GetProperty(nameof(Institucion.Id))!.SetValue(i, Guid.Parse(Aurora));
            c.Instituciones.Add(i);
            c.SaveChanges();
        }
    }

    public void Dispose() => _fabrica.Dispose();

    private static string Token(string rol, string jurisdiccion, string? sub = null, RSA? llave = null)
    {
        var ahora = DateTimeOffset.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims:
        [
            new Claim("sub", sub ?? Guid.NewGuid().ToString()), new Claim("role", rol), new Claim("jurisdiction", jurisdiccion),
            new Claim("iss", "https://identidad.redvital.local"), new Claim("aud", "redvital"),
            new Claim("iat", ahora.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new Claim("exp", ahora.AddMinutes(15).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
        ], signingCredentials: new SigningCredentials(new RsaSecurityKey(llave ?? Llave) { KeyId = "prueba" }, SecurityAlgorithms.RsaSha256)));
    }

    private HttpClient Cliente(string? token)
    {
        var c = _fabrica.CreateClient();
        if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        c.DefaultRequestHeaders.Add("X-Correlacion-Id", "corr-api");
        return c;
    }

    private static async Task<JsonElement> Cuerpo(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task AssertProblema(HttpResponseMessage r, HttpStatusCode estado, string tipo)
    {
        Assert.Equal(estado, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType!.MediaType);
        var p = await Cuerpo(r);
        Assert.Equal(tipo, p.GetProperty("tipo").GetString());
        Assert.Equal((int)estado, p.GetProperty("estado").GetInt32());
        Assert.Equal("corr-api", p.GetProperty("correlacion_id").GetString());
    }

    [Fact]
    public async Task Sin_token_o_con_token_alterado_es_401()
    {
        await AssertProblema(await Cliente(null).GetAsync("/v1/territorios"), HttpStatusCode.Unauthorized, "sesion-invalida");
        var ajena = Token(Roles.AdminNacional, "territorio:/00", llave: RSA.Create(2048));
        await AssertProblema(await Cliente(ajena).GetAsync("/v1/territorios"), HttpStatusCode.Unauthorized, "sesion-invalida");
    }

    [Fact]
    public async Task El_coordinador_lista_su_departamento_y_fuera_de_el_recibe_403()
    {
        var c = Cliente(Token(Roles.Coordinador, "territorio:/00/05"));
        var r = await c.GetAsync("/v1/territorios?nivel=departamental");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var pagina = await Cuerpo(r);
        Assert.Equal("/00/05", pagina.GetProperty("elementos")[0].GetProperty("ruta").GetString());
        Assert.Equal(1, pagina.GetProperty("total").GetInt32());

        await AssertProblema(await c.GetAsync("/v1/instituciones?territorio_ruta=/00/11"), HttpStatusCode.Forbidden, "fuera-de-jurisdiccion");
        await AssertProblema(await c.GetAsync("/v1/territorios?nivel=otro"), HttpStatusCode.BadRequest, "peticion-invalida");
    }

    [Fact]
    public async Task Cualquier_perfil_con_cuenta_resuelve_una_institucion()
    {
        var r = await Cliente(Token(Roles.Donante, "")).GetAsync($"/v1/instituciones/{Aurora}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var i = await Cuerpo(r);
        Assert.Equal("/00/05/05001", i.GetProperty("territorio_ruta").GetString());
        Assert.Equal("banco_de_sangre", i.GetProperty("tipo").GetString());
        await AssertProblema(await Cliente(Token(Roles.Donante, "")).GetAsync($"/v1/instituciones/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "no-encontrado");
    }

    [Fact]
    public async Task Alta_de_territorio_solo_U6_y_sin_propiedades_desconocidas()
    {
        var u6 = Cliente(Token(Roles.AdminNacional, "territorio:/00"));
        var codigo = $"05{Random.Shared.Next(900, 999)}";
        var r = await u6.PostAsJsonAsync("/v1/territorios", new { codigo_divipola = codigo, nombre = "Municipio API" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal($"/00/05/{codigo}", (await Cuerpo(r)).GetProperty("ruta").GetString());

        // La ruta no se acepta en el cuerpo: la calcula el servicio.
        await AssertProblema(await u6.PostAsJsonAsync("/v1/territorios", new { codigo_divipola = "05998", nombre = "X", ruta = "/00/05/05998" }),
            HttpStatusCode.BadRequest, "peticion-invalida");
        await AssertProblema(await Cliente(Token(Roles.Coordinador, "territorio:/00/05")).PostAsJsonAsync("/v1/territorios",
            new { codigo_divipola = "05997", nombre = "X" }), HttpStatusCode.Forbidden, "operacion-no-permitida");
    }

    [Fact]
    public async Task El_coordinador_da_de_alta_y_cambia_una_institucion_de_su_departamento()
    {
        var u5 = Cliente(Token(Roles.Coordinador, "territorio:/00/05"));
        var territorios = await Cuerpo(await u5.GetAsync("/v1/territorios?nivel=municipal&tamano=500"));
        var bello = territorios.GetProperty("elementos").EnumerateArray().Single(t => t.GetProperty("codigo_divipola").GetString() == "05088");
        var codigo = $"API-{Random.Shared.Next(10000, 99999)}";
        var alta = await u5.PostAsJsonAsync("/v1/instituciones", new
        {
            codigo, nombre = "Banco API (ficticio)", tipo = "servicio_transfusion", territorio_id = bello.GetProperty("id").GetGuid(),
        });
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var id = (await Cuerpo(alta)).GetProperty("id").GetGuid();

        await AssertProblema(await u5.PutAsJsonAsync($"/v1/instituciones/{id}", new { tipo = "banco_de_sangre" }), HttpStatusCode.Conflict, "conflicto-de-estado");
        var cambio = await u5.PutAsJsonAsync($"/v1/instituciones/{id}", new { nombre = "Banco API renombrado (ficticio)" });
        Assert.Equal("Banco API renombrado (ficticio)", (await Cuerpo(cambio)).GetProperty("nombre").GetString());
        await AssertProblema(await Cliente(Token(Roles.AdminBanco, $"institucion:{Aurora}")).PutAsJsonAsync($"/v1/instituciones/{id}",
            new { nombre = "No" }), HttpStatusCode.Forbidden, "operacion-no-permitida");
    }

    [Fact]
    public async Task El_coordinador_ve_salir_el_EV07_de_lo_que_da_de_alta()
    {
        var u5 = Cliente(Token(Roles.Coordinador, "territorio:/00/05"));
        var territorios = await Cuerpo(await u5.GetAsync("/v1/territorios?nivel=municipal&tamano=500"));
        var envigado = territorios.GetProperty("elementos").EnumerateArray().Single(t => t.GetProperty("codigo_divipola").GetString() == "05266");
        var alta = await u5.PostAsJsonAsync("/v1/instituciones", new
        {
            codigo = $"API-{Random.Shared.Next(10000, 99999)}", nombre = "Banco Evento (ficticio)", tipo = "banco_de_sangre",
            territorio_id = envigado.GetProperty("id").GetGuid(),
        });
        var id = (await Cuerpo(alta)).GetProperty("id").GetGuid();
        await using var c = _db.Contexto();
        var e = c.EventosSalida.Single(x => x.Clave == id.ToString());
        Assert.Equal(("institution.changed", "corr-api"), (e.Tipo, e.CorrelacionId));
    }

    [Fact]
    public async Task El_auditor_lee_la_bitacora_compuesta_y_no_escribe_nada()
    {
        // Un registro de Donación que ya llegó por EV-10.
        await using (var c = _db.Contexto())
        {
            await new BitacoraRecibida(c).AnexarAsync(Guid.NewGuid(), "donacion", new RegistroAuditoria(Guid.NewGuid(), "usuario", "op",
                Roles.Operador, null, "registrar_donacion", "donacion", null, "permitido", "corr-api", null, DateTimeOffset.UtcNow.AddMinutes(-1)),
                CancellationToken.None);
        }
        var u7 = Cliente(Token(Roles.Auditor, "territorio:/00"));
        var r = await u7.GetAsync("/v1/auditoria?correlacion_id=corr-api&tamano=20");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var b = await Cuerpo(r);
        Assert.Contains(b.GetProperty("registros").EnumerateArray(), x =>
            x.GetProperty("contexto").GetString() == "donacion" && x.GetProperty("operacion").GetString() == "registrar_donacion");
        var series = b.GetProperty("series").EnumerateArray().ToDictionary(x => x.GetProperty("contexto").GetString()!, x => x.GetProperty("al_dia_hasta"));
        Assert.Equal(["campanias", "donacion", "identidad", "institucional"], series.Keys.Order());
        Assert.NotEqual(JsonValueKind.Null, series["donacion"].ValueKind);
        Assert.NotEqual(JsonValueKind.Null, series["institucional"].ValueKind);

        // RI-04: el auditor no tiene ninguna escritura.
        await AssertProblema(await u7.PostAsJsonAsync("/v1/territorios", new { codigo_divipola = "05996", nombre = "X" }), HttpStatusCode.Forbidden, "operacion-no-permitida");
        await AssertProblema(await u7.PostAsJsonAsync("/v1/instituciones", new { codigo = "AUD-1", nombre = "X", tipo = "banco_de_sangre", territorio_id = Guid.NewGuid() }),
            HttpStatusCode.Forbidden, "operacion-no-permitida");
        // Su propia consulta quedó en la serie institucional.
        var otra = await Cuerpo(await u7.GetAsync("/v1/auditoria?recurso_tipo=bitacora&tamano=5"));
        Assert.Contains(otra.GetProperty("registros").EnumerateArray(), x =>
            x.GetProperty("contexto").GetString() == "institucional" && x.GetProperty("operacion").GetString() == "consultar_bitacora");
    }

    [Fact]
    public async Task Los_indicadores_dicen_de_cuando_es_la_fotografia_y_sin_ella_no_inventan_cifras()
    {
        var u4 = Cliente(Token(Roles.AdminBanco, $"institucion:{Aurora}"));
        var antes = await Cuerpo(await u4.GetAsync("/v1/indicadores?desde=2026-10-01&hasta=2026-10-31&agrupar_por=dia"));
        if (antes.GetProperty("actualizado_en").ValueKind == JsonValueKind.Null)
            Assert.Empty(antes.GetProperty("captacion").EnumerateArray());

        var generado = DateTimeOffset.UtcNow;
        await using (var c = _db.Contexto())
        {
            await new IndicadoresProyectados(c).ReemplazarAsync(Guid.NewGuid(), new FotografiaIndicadores(Guid.Parse(Aurora), generado,
                [new DiaCaptacion(new DateOnly(2026, 10, 20), 6, 18, 2, 0)], [new ExistenciaRecibida("plasma", "AB-", 3, 1, false)]),
                CancellationToken.None);
        }
        var r = await u4.GetAsync("/v1/indicadores?desde=2026-10-01&hasta=2026-10-31&agrupar_por=dia");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var i = await Cuerpo(r);
        Assert.Equal(generado.ToUnixTimeSeconds(), i.GetProperty("actualizado_en").GetDateTimeOffset().ToUnixTimeSeconds());
        var dia = Assert.Single(i.GetProperty("captacion").EnumerateArray());
        Assert.Equal(("2026-10-20", 2), (dia.GetProperty("periodo").GetString(), dia.GetProperty("unidades_no_aptas").GetInt32()));
        Assert.Equal("plasma", Assert.Single(i.GetProperty("inventario").EnumerateArray()).GetProperty("componente").GetString());
    }
}
