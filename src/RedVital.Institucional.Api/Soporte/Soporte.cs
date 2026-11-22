// RedVital — institutional-service. Correlación (T-305.5, lado servicio),
// problemas RFC 9457 con el catálogo común de los servicios y arranque en
// contenedor. Misma forma que Campañas e Identidad.

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RedVital.Institucional.Aplicacion;
using RedVital.Institucional.Dominio;

namespace RedVital.Institucional.Api.Soporte;

public static class Correlacion
{
    public const string Cabecera = "X-Correlacion-Id";

    public static IApplicationBuilder UsarCorrelacion(this IApplicationBuilder app) =>
        app.Use(async (ctx, siguiente) =>
        {
            var valor = ctx.Request.Headers[Cabecera].ToString();
            if (string.IsNullOrWhiteSpace(valor) || valor.Length > 36) valor = Guid.NewGuid().ToString();
            ctx.Items[Cabecera] = valor;
            ctx.Response.OnStarting(() => { ctx.Response.Headers[Cabecera] = valor; return Task.CompletedTask; });
            await siguiente();
        });

    public static string CorrelacionId(this HttpContext ctx) => ctx.Items[Cabecera] as string ?? Guid.NewGuid().ToString();
}

public static class Peticion
{
    /// <summary>Solicitante, correlación y origen (el que entrega el gateway).</summary>
    public static ContextoPeticion Contexto(this HttpContext http)
    {
        var u = http.User;
        var solicitante = u.Identity?.IsAuthenticated == true
            ? new Solicitante(u.FindFirstValue("sub"), u.FindFirstValue("role"), Jurisdiccion(u))
            : Solicitante.Anonimo;
        var origen = http.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
        if (string.IsNullOrEmpty(origen)) origen = http.Connection.RemoteIpAddress?.ToString() ?? "";
        return new ContextoPeticion(solicitante, http.CorrelacionId(), origen.Length == 0 ? null : origen);
    }

    /// <summary>La reivindicación llega como texto o, según el DD, como lista de un elemento.</summary>
    private static string? Jurisdiccion(ClaimsPrincipal u)
    {
        var valores = u.FindAll("jurisdiction").Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        return valores.Count == 1 ? valores[0] : null;
    }
}

public static class Problemas
{
    public static IActionResult Desde<T>(Resultado<T> r, string correlacionId) => r.Fallo switch
    {
        Fallo.NoPermitido => Crear(403, "operacion-no-permitida", "Operación no permitida", "El rol no autoriza esta operación.", correlacionId),
        Fallo.FueraDeJurisdiccion => Crear(403, "fuera-de-jurisdiccion", "Fuera de jurisdicción", "Lo pedido está fuera de su jurisdicción.", correlacionId),
        Fallo.NoEncontrado => Crear(404, "no-encontrado", "No encontrado", "El recurso no existe dentro de su alcance.", correlacionId),
        Fallo.Conflicto => Crear(409, "conflicto-de-estado", "Conflicto de estado", r.Detalle ?? "El estado actual no permite la operación.", correlacionId),
        _ => Crear(400, "peticion-invalida", "Petición inválida", r.Detalle ?? "La petición no es válida.", correlacionId),
    };

    public static IActionResult Crear(int estado, string tipo, string titulo, string detalle, string correlacionId) =>
        new ObjectResult(new { tipo, titulo, estado, detalle, correlacion_id = correlacionId })
        { StatusCode = estado, ContentTypes = { "application/problem+json" } };

    public static Task EscribirAsync(HttpContext ctx, int estado, string tipo, string titulo, string detalle)
    {
        ctx.Response.StatusCode = estado;
        ctx.Response.ContentType = "application/problem+json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new { tipo, titulo, estado, detalle, correlacion_id = ctx.CorrelacionId() }));
    }
}

public static class CadenaConexion
{
    /// <summary>La contraseña llega como secreto en archivo, nunca en la cadena versionada.</summary>
    public static string? ConClave(string? cadena, string? clave) =>
        cadena is null || string.IsNullOrWhiteSpace(clave) || cadena.Contains("Password=", StringComparison.OrdinalIgnoreCase)
            ? cadena
            : $"{cadena.TrimEnd(';')};Password={clave.Trim()}";
}

public static class Salud
{
    /// <summary>La imagen no tiene curl: el propio ejecutable consulta /salud.</summary>
    public static async Task<int> ComprobarAsync()
    {
        try
        {
            using var cliente = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var puerto = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080";
            var respuesta = await cliente.GetAsync($"http://127.0.0.1:{puerto}/salud");
            return respuesta.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }
}
