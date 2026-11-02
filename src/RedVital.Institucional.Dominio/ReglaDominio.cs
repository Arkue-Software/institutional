// RedVital — institutional-service
namespace RedVital.Institucional.Dominio;

/// <summary>Una invariante del dominio que la petición no cumple (400 con el motivo).</summary>
public sealed class ReglaDominio(string mensaje) : Exception(mensaje);
