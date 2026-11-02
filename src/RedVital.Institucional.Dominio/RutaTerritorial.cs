// RedVital — institutional-service
// La ruta territorial (DD Sección 7, IN-13, PD-07): se deriva del código
// DIVIPOLA por regla fija y nunca se recibe de afuera.
//   nación /00 · departamento /00/05 · municipio /00/05/05001

using System.Text.RegularExpressions;

namespace RedVital.Institucional.Dominio;

public enum NivelTerritorial { Nacional, Departamental, Municipal }

public static class Niveles
{
    public static string Codigo(NivelTerritorial n) => n switch
    {
        NivelTerritorial.Nacional => "nacional",
        NivelTerritorial.Departamental => "departamental",
        _ => "municipal",
    };

    public static NivelTerritorial? Desde(string? codigo) => codigo switch
    {
        "nacional" => NivelTerritorial.Nacional,
        "departamental" => NivelTerritorial.Departamental,
        "municipal" => NivelTerritorial.Municipal,
        _ => null,
    };
}

public sealed partial record RutaTerritorial
{
    public const string Nacion = "/00";

    public string Valor { get; }

    private RutaTerritorial(string valor) => Valor = valor;

    /// <summary>Ruta derivada de un código DIVIPOLA válido (00, dos o cinco dígitos).</summary>
    public static RutaTerritorial DeCodigo(string codigo)
    {
        if (codigo == "00") return new(Nacion);
        if (Departamento().IsMatch(codigo)) return new($"{Nacion}/{codigo}");
        if (Municipio().IsMatch(codigo)) return new($"{Nacion}/{codigo[..2]}/{codigo}");
        throw new ReglaDominio("El código DIVIPOLA debe ser 00, de dos dígitos o de cinco dígitos.");
    }

    /// <summary>
    /// Interpreta una ruta recibida (de un token o de un parámetro). Solo
    /// acepta las que la regla de derivación produce: en un municipio, los dos
    /// primeros dígitos son los de su departamento.
    /// </summary>
    public static bool TryParse(string? texto, out RutaTerritorial ruta)
    {
        ruta = null!;
        if (string.IsNullOrEmpty(texto) || !Forma().IsMatch(texto)) return false;
        var codigo = texto.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
        var derivada = DeCodigo(codigo == "00" ? "00" : codigo);
        if (derivada.Valor != texto) return false;
        ruta = derivada;
        return true;
    }

    public string CodigoDivipola => Valor.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];

    public NivelTerritorial Nivel => Valor.Count(c => c == '/') switch
    {
        1 => NivelTerritorial.Nacional,
        2 => NivelTerritorial.Departamental,
        _ => NivelTerritorial.Municipal,
    };

    /// <summary>
    /// «¿El territorio está dentro de este?». Se compara con «/» al final para
    /// que /00/1 no alcance a /00/11.
    /// </summary>
    public bool Contiene(RutaTerritorial otra) => otra.Valor == Valor || otra.Valor.StartsWith(Valor + "/", StringComparison.Ordinal);

    public override string ToString() => Valor;

    [GeneratedRegex("^[0-9]{2}$")] private static partial Regex Departamento();
    [GeneratedRegex("^[0-9]{5}$")] private static partial Regex Municipio();
    [GeneratedRegex("^/00(/[0-9]{2}(/[0-9]{5})?)?$")] private static partial Regex Forma();
}
