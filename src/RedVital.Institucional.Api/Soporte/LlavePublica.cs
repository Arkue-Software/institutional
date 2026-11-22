// RedVital — institutional-service. Llave pública de firma de Identidad, montada como
// secreto: con ella se validan los tokens sin consultar a Identidad (EC-03).

using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace RedVital.Institucional.Api.Soporte;

public static class LlavePublica
{
    /// <summary>Lee la llave RSA en formato PEM. Sin ella el servicio no debe arrancar.</summary>
    /// <exception cref="InvalidOperationException">El archivo no existe o no contiene una llave pública RSA.</exception>
    public static RsaSecurityKey Cargar(string archivo)
    {
        if (!File.Exists(archivo))
            throw new InvalidOperationException($"No existe la llave pública de firma de Identidad en {archivo}.");
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(File.ReadAllText(archivo));
        }
        catch (ArgumentException e)
        {
            rsa.Dispose();
            throw new InvalidOperationException($"{archivo} no contiene una llave pública RSA en formato PEM.", e);
        }
        var bits = rsa.KeySize;
        if (bits < 2048)
        {
            rsa.Dispose();
            throw new InvalidOperationException($"La llave pública de {archivo} tiene {bits} bits; se exigen al menos 2048.");
        }
        return new RsaSecurityKey(rsa);
    }
}
