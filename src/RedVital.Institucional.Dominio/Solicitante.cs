// RedVital — institutional-service
// Quién hace la petición, leído del token que este servicio revalida (EC-03).
// La jurisdicción viaja en el token como «institucion:<uuid>» o
// «territorio:<ruta>» (ADR-008).

namespace RedVital.Institucional.Dominio;

public static class Roles
{
    public const string Donante = "donante";
    public const string Operador = "operador";
    public const string AdminBanco = "admin_banco";
    public const string Coordinador = "coordinador";
    public const string AdminNacional = "admin_nacional";
    public const string Auditor = "auditor";
    public const string Servicio = "servicio";
}

public sealed record Solicitante(string? Sub, string? Rol, string? Jurisdiccion)
{
    public static readonly Solicitante Anonimo = new(null, null, null);

    public bool EsAnonimo => Rol is null;

    public bool Es(params string[] roles) => Rol is not null && roles.Contains(Rol);


    public Guid? InstitucionJurisdiccion =>
        Jurisdiccion is not null && Jurisdiccion.StartsWith("institucion:", StringComparison.Ordinal) &&
        Guid.TryParse(Jurisdiccion["institucion:".Length..], out var id) ? id : null;

    public RutaTerritorial? RutaJurisdiccion =>
        Jurisdiccion is not null && Jurisdiccion.StartsWith("territorio:", StringComparison.Ordinal) &&
        RutaTerritorial.TryParse(Jurisdiccion["territorio:".Length..], out var r) ? r : null;

    /// <summary>Cuenta del usuario, que Identidad emite como UUID.</summary>
    public Guid? CuentaId => Guid.TryParse(Sub, out var id) ? id : null;
}
