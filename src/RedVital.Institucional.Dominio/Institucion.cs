// RedVital — institutional-service
// institucion (DD Sección 7): banco de sangre o servicio de transfusión
// adscrito a un municipio (RF-09, EC-39, EC-42).

using System.Text.RegularExpressions;

namespace RedVital.Institucional.Dominio;

public enum TipoInstitucion { BancoDeSangre, ServicioTransfusion }

public static class Tipos
{
    public static string Codigo(TipoInstitucion t) => t == TipoInstitucion.BancoDeSangre ? "banco_de_sangre" : "servicio_transfusion";

    public static TipoInstitucion? Desde(string? codigo) => codigo switch
    {
        "banco_de_sangre" => TipoInstitucion.BancoDeSangre,
        "servicio_transfusion" => TipoInstitucion.ServicioTransfusion,
        _ => null,
    };
}

public sealed partial class Institucion
{
    public Guid Id { get; private set; }
    public string Codigo { get; private set; } = "";
    public string Nombre { get; private set; } = "";
    public TipoInstitucion Tipo { get; private set; }
    public Guid TerritorioId { get; private set; }
    public string? Direccion { get; private set; }
    public bool Vigente { get; private set; } = true;
    public DateTimeOffset CreadoEn { get; private set; }
    public Guid CreadoPor { get; private set; }

    private Institucion() { }

    /// <summary>
    /// Alta (EC-42): nace vigente, en un municipio, sin unidades ni umbrales, y
    /// nada anterior a <paramref name="ahora"/> se le atribuye.
    /// </summary>
    public static Institucion Crear(string codigo, string nombre, TipoInstitucion tipo, Territorio municipio,
        string? direccion, Guid creadoPor, DateTimeOffset ahora)
    {
        if (municipio.Nivel != NivelTerritorial.Municipal)
            throw new ReglaDominio("Una institución pertenece a un municipio.");
        if (!municipio.Vigente)
            throw new ReglaDominio("El municipio no está vigente.");
        var limpio = codigo?.Trim() ?? "";
        if (!CodigoValido().IsMatch(limpio))
            throw new ReglaDominio("El código de habilitación tiene de 4 a 20 letras, dígitos o guiones.");
        return new Institucion
        {
            Id = Guid.NewGuid(),
            Codigo = limpio,
            Nombre = Territorio.NombreValido(nombre, 160),
            Tipo = tipo,
            TerritorioId = municipio.Id,
            Direccion = DireccionValida(direccion),
            CreadoEn = ahora,
            CreadoPor = creadoPor,
        };
    }

    /// <summary>
    /// Cambio o baja. El tipo no cambia: otros servicios ya decidieron sobre él
    /// (custodia de unidades, EC-39), y la base también lo impide.
    /// </summary>
    public void Modificar(string? nombre, string? direccion, bool? vigente)
    {
        if (nombre is not null) Nombre = Territorio.NombreValido(nombre, 160);
        if (direccion is not null) Direccion = DireccionValida(direccion);
        if (vigente is not null) Vigente = vigente.Value;
    }

    private static string? DireccionValida(string? direccion)
    {
        var limpia = direccion?.Trim();
        if (string.IsNullOrEmpty(limpia)) return null;
        if (limpia.Length > 200) throw new ReglaDominio("La dirección admite hasta 200 caracteres.");
        return limpia;
    }

    [GeneratedRegex("^[A-Za-z0-9-]{4,20}$")] private static partial Regex CodigoValido();
}
