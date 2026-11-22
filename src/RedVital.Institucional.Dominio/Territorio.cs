// RedVital — institutional-service
// territorio (DD Sección 7): jerarquía nacional, departamental y municipal.

namespace RedVital.Institucional.Dominio;

public sealed class Territorio
{
    public Guid Id { get; private set; }
    public string CodigoDivipola { get; private set; } = "";
    public string Nombre { get; private set; } = "";
    public NivelTerritorial Nivel { get; private set; }
    public Guid? PadreId { get; private set; }
    public string Ruta { get; private set; } = "";
    public bool Vigente { get; private set; } = true;

    private Territorio() { }

    public RutaTerritorial RutaTerritorial
    {
        get
        {
            RutaTerritorial.TryParse(Ruta, out var r);
            return r;
        }
    }

    /// <summary>
    /// Alta de un territorio. El padre es el del nivel inmediato superior y
    /// contiene la ruta nueva; la ruta se calcula (IN-13).
    /// </summary>
    public static Territorio Crear(string codigo, string nombre, Territorio padre)
    {
        var ruta = RutaTerritorial.DeCodigo(codigo.Trim());
        if (ruta.Nivel == NivelTerritorial.Nacional)
            throw new ReglaDominio("El nivel nacional ya existe y es la raíz única.");
        var esperado = ruta.Nivel == NivelTerritorial.Departamental ? NivelTerritorial.Nacional : NivelTerritorial.Departamental;
        if (padre.Nivel != esperado || !padre.RutaTerritorial.Contiene(ruta))
            throw new ReglaDominio("El territorio no corresponde al territorio padre indicado.");
        return new Territorio
        {
            Id = Guid.NewGuid(),
            CodigoDivipola = ruta.CodigoDivipola,
            Nombre = NombreValido(nombre, 120),
            Nivel = ruta.Nivel,
            PadreId = padre.Id,
            Ruta = ruta.Valor,
        };
    }

    internal static string NombreValido(string? nombre, int maximo)
    {
        var limpio = nombre?.Trim() ?? "";
        if (limpio.Length == 0) throw new ReglaDominio("El nombre es obligatorio.");
        if (limpio.Length > maximo) throw new ReglaDominio($"El nombre admite hasta {maximo} caracteres.");
        return limpio;
    }
}
