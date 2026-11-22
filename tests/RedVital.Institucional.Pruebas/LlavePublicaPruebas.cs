// La llave pública de Identidad se valida al arrancar: sin ella, o con una
// llave que no sirve, el servicio no arranca.

using System.Security.Cryptography;
using RedVital.Institucional.Api.Soporte;
using Xunit;

namespace RedVital.Institucional.Pruebas;

public class LlavePublicaPruebas
{
    private static string Archivo(string contenido)
    {
        var ruta = Path.Combine(Directory.CreateTempSubdirectory("llave-").FullName, "identidad_llave_publica");
        File.WriteAllText(ruta, contenido);
        return ruta;
    }

    [Fact]
    public void Una_llave_RSA_de_2048_bits_se_carga()
    {
        using var rsa = RSA.Create(2048);
        Assert.Equal(2048, LlavePublica.Cargar(Archivo(rsa.ExportSubjectPublicKeyInfoPem())).KeySize);
    }

    [Fact]
    public void Sin_archivo_contenido_valido_o_tamano_suficiente_no_arranca()
    {
        Assert.Throws<InvalidOperationException>(() => LlavePublica.Cargar("/no/existe/identidad_llave_publica"));
        Assert.Throws<InvalidOperationException>(() => LlavePublica.Cargar(Archivo("esto no es una llave")));
        using var corta = RSA.Create(1024);
        Assert.Throws<InvalidOperationException>(() => LlavePublica.Cargar(Archivo(corta.ExportSubjectPublicKeyInfoPem())));
    }
}
