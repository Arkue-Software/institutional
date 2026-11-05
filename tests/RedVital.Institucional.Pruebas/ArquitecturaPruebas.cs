// Regla de dependencias de la arquitectura hexagonal: el dominio no depende
// de nada, y los casos de uso solo del dominio y de sus propios puertos. La
// base, Kafka, HTTP y ASP.NET entran por adaptadores (inversión de dependencias).

using NetArchTest.Rules;
using RedVital.Institucional.Aplicacion.CasosDeUso;
using RedVital.Institucional.Dominio;
using Xunit;

namespace RedVital.Institucional.Pruebas;

public class ArquitecturaPruebas
{
    private static readonly string[] Tecnologia =
    [
        "Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.AspNetCore", "System.Net.Http", "Confluent.Kafka",
        "RedVital.Institucional.Infraestructura", "RedVital.Institucional.Api",
    ];

    [Fact]
    public void El_dominio_no_depende_de_aplicacion_ni_de_tecnologia()
    {
        var r = Types.InAssembly(typeof(Territorio).Assembly).ShouldNot()
            .HaveDependencyOnAny([.. Tecnologia, "RedVital.Institucional.Aplicacion"]).GetResult();
        Assert.True(r.IsSuccessful, string.Join(", ", r.FailingTypeNames ?? []));
    }

    [Fact]
    public void Los_casos_de_uso_no_dependen_de_adaptadores()
    {
        var r = Types.InAssembly(typeof(ConsultarBitacora).Assembly).ShouldNot()
            .HaveDependencyOnAny(Tecnologia).GetResult();
        Assert.True(r.IsSuccessful, string.Join(", ", r.FailingTypeNames ?? []));
    }

    [Fact]
    public void Los_controladores_no_tocan_la_persistencia()
    {
        var r = Types.InAssembly(typeof(Program).Assembly).That().ResideInNamespace("RedVital.Institucional.Api.Controllers")
            .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "RedVital.Institucional.Infraestructura").GetResult();
        Assert.True(r.IsSuccessful, string.Join(", ", r.FailingTypeNames ?? []));
    }
}
