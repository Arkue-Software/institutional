// RedVital — institutional-service (DD Sección 7): M6 territorios e
// instituciones, M7 indicadores y la consulta compuesta de la bitácora (ST2).
// Con los demás servicios solo habla por Kafka: publica cada alta o cambio de
// institución (EV-07) y arma indicadores y bitácora con lo que recibe (EV-09 y
// EV-10). No llama a ningún servicio ni recibe llamadas de ellos.
//
// Validación de token propia (EC-03), igual que Campañas:
//  - Development: con la llave PÚBLICA de prueba (sin Identidad corriendo).
//  - QA/Production: con la llave pública de firma de Identidad, montada como
//    secreto (identidad_llave_publica).

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RedVital.Institucional.Api.Mantenimiento;
using RedVital.Institucional.Api.Mensajeria;
using RedVital.Institucional.Api.Soporte;
using RedVital.Institucional.Aplicacion.CasosDeUso;
using RedVital.Institucional.Aplicacion.Puertos;
using RedVital.Institucional.Infraestructura.Persistencia;

if (args.Contains("--salud")) return await Salud.ComprobarAsync();

var builder = WebApplication.CreateBuilder(args);
var conf = builder.Configuration;

// Secretos montados como archivos (/run/secrets/<nombre>): institucional_servicio y kafka_institucional.
conf.AddKeyPerFile(conf["Secretos:Directorio"] ?? "/run/secrets", optional: true);

// ---------------------------------------------------------------- Puertos
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<InstitucionalDbContext>(o =>
    o.UseNpgsql(CadenaConexion.ConClave(conf.GetConnectionString("Institucional"), conf["institucional_servicio"])
        ?? throw new InvalidOperationException("Falta ConnectionStrings:Institucional.")));
builder.Services.AddScoped<IRepositorioTerritorios, RepositorioTerritorios>();
builder.Services.AddScoped<IRepositorioInstituciones, RepositorioInstituciones>();
builder.Services.AddScoped<IConsultaCobertura, ConsultaCobertura>();
builder.Services.AddScoped<IRegistroAuditoria, RegistroAuditoriaInst>();

builder.Services.AddScoped<IBandejaSalida, BandejaSalida>();
builder.Services.AddScoped<IndicadoresProyectados>();
builder.Services.AddScoped<IConsultaIndicadores>(sp => sp.GetRequiredService<IndicadoresProyectados>());
builder.Services.AddScoped<IProyeccionIndicadores>(sp => sp.GetRequiredService<IndicadoresProyectados>());
builder.Services.AddScoped<IProyeccionBitacora, BitacoraRecibida>();
// Las cuatro series de la bitácora: la propia y las copias de las otras tres, con el mismo contrato.
builder.Services.AddScoped<IFuenteAuditoria, FuenteAuditoriaPropia>();
foreach (var contexto in AplicarAuditoriaRegistrada.Contextos)
    builder.Services.AddScoped<IFuenteAuditoria>(sp => new FuenteAuditoriaRecibida(contexto, sp.GetRequiredService<InstitucionalDbContext>()));

// ----------------------------------------------------------- Casos de uso
builder.Services.AddScoped<ResolutorJurisdiccion>();
builder.Services.AddScoped<Denegaciones>();
builder.Services.AddScoped<ConsultarTerritorios>();
builder.Services.AddScoped<CrearTerritorio>();
builder.Services.AddScoped<ConsultarInstituciones>();
builder.Services.AddScoped<ResolverInstitucion>();
builder.Services.AddScoped<CrearInstitucion>();
builder.Services.AddScoped<ModificarInstitucion>();
builder.Services.AddScoped<ConsultarIndicadores>();
builder.Services.AddScoped<ConsultarBitacora>();
builder.Services.AddScoped<AplicarIndicadoresActualizados>();
builder.Services.AddScoped<AplicarAuditoriaRegistrada>();
builder.Services.AddHostedService<DepuradorRegistrosTecnicos>(); // bandeja publicada y eventos procesados vencidos
builder.Services.AddHealthChecks();

// ------------------------------------------------------------------ Kafka
// Único canal con los demás servicios (ADR-018). Si el broker no responde, los
// eventos de salida esperan en la base y el API sigue atendiendo.
var kafka = conf.GetSection("Kafka").Get<OpcionesKafka>() ?? new OpcionesKafka();
kafka.Clave ??= conf["kafka_institucional"];
if (kafka.Habilitado)
{
    builder.Services.AddSingleton(kafka);
    builder.Services.AddSingleton<ProductorKafka>();
    builder.Services.AddHostedService<PublicadorBandeja>();
    builder.Services.AddHostedService<ConsumidorIndicadores>();
    builder.Services.AddHostedService<ConsumidorAuditoria>();
}

// --------------------------------------------------------------- Seguridad
// La llave pública de firma de Identidad se lee al arrancar: si falta o no es
// válida, el servicio no arranca, en vez de responder 500 a cada petición.
var llavePublicaDev = conf["Jwt:LlavePublicaDesarrollo"];
var llaveFirma = LlavePublica.Cargar(builder.Environment.IsDevelopment() && !string.IsNullOrEmpty(llavePublicaDev)
    ? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, llavePublicaDev))
    : conf["Jwt:LlavePublica"] ?? "/run/secrets/identidad_llave_publica");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = conf["Jwt:Emisor"],
        ValidAudience = conf["Jwt:Audiencia"],
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        NameClaimType = "sub",
        RoleClaimType = "role",
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    o.TokenValidationParameters.IssuerSigningKey = llaveFirma;

    o.Events = new JwtBearerEvents
    {
        OnChallenge = async c =>
        {
            c.HandleResponse();
            await Problemas.EscribirAsync(c.HttpContext, 401, "sesion-invalida", "Sesión inválida", "El token es inválido o expiró.");
        },
        OnForbidden = c => Problemas.EscribirAsync(c.HttpContext, 403, "operacion-no-permitida", "Operación no permitida",
            "El rol no autoriza esta operación."),
    };
});
builder.Services.AddAuthorization();

// Contrato en snake_case; una propiedad desconocida en el cuerpo es un 400
// (mismo criterio que Donación), y el 400 tiene la forma de los problemas.
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        o.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    })
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = c =>
        Problemas.Crear(400, "peticion-invalida", "Petición inválida",
            "El cuerpo o los parámetros no cumplen el contrato.", c.HttpContext.CorrelacionId()));

var app = builder.Build();
app.UsarCorrelacion();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/salud");
await app.RunAsync();
return 0;

public partial class Program { }
