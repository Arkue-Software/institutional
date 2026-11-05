// RedVital — institutional-service. Tipos comunes de los casos de uso.

using RedVital.Institucional.Dominio;

namespace RedVital.Institucional.Aplicacion;

public enum Fallo { Ninguno, Invalido, NoPermitido, FueraDeJurisdiccion, NoEncontrado, Conflicto }

public sealed record Resultado<T>(T? Valor, Fallo Fallo = Fallo.Ninguno, string? Detalle = null)
{
    public bool Exitoso => Fallo == Fallo.Ninguno;
    public static Resultado<T> Ok(T valor) => new(valor);
    public static Resultado<T> Error(Fallo fallo, string? detalle = null) => new(default, fallo, detalle);
}

/// <summary>Lo que el adaptador web sabe de la petición: quién la hace, su correlación y su origen.</summary>
public sealed record ContextoPeticion(Solicitante Solicitante, string CorrelacionId, string? Origen);

public sealed record Paginado<T>(IReadOnlyList<T> Elementos, long Total, int Pagina, int Tamano)
{
    public Paginado<R> Map<R>(Func<T, R> f) => new(Elementos.Select(f).ToList(), Total, Pagina, Tamano);
}

public static class Paginacion
{
    public const int Maxima = 500;

    public static bool Validar(int? pagina, int? tamano, int porDefecto, out int p, out int t)
    {
        p = pagina ?? 1;
        t = tamano ?? porDefecto;
        return p >= 1 && t >= 1 && t <= Maxima;
    }

    public const string Mensaje = "pagina desde 1 y tamano entre 1 y 500.";
}
