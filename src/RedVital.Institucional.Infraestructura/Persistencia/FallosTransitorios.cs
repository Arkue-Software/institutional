// RedVital — institutional-service. Distingue el fallo de una dependencia —la base
// no responde— del fallo del propio mensaje. El primero no es culpa del
// evento: el consumidor lo vuelve a intentar cuando la base vuelva, sin
// mandarlo a mensajes muertos (DD 14.1).

using Npgsql;

namespace RedVital.Institucional.Infraestructura.Persistencia;

public static class FallosTransitorios
{
    /// <summary>
    /// Verdadero si en la cadena de excepciones hay un fallo transitorio de
    /// PostgreSQL (conexión caída, base reiniciándose, sin recursos, bloqueo) o
    /// un plazo vencido. EF Core envuelve la excepción de Npgsql, por eso se
    /// recorren las causas.
    /// </summary>
    public static bool EsDeLaBase(Exception excepcion)
    {
        for (Exception? actual = excepcion; actual is not null; actual = actual.InnerException)
        {
            if (actual is NpgsqlException { IsTransient: true } or TimeoutException) return true;
        }
        return false;
    }
}
