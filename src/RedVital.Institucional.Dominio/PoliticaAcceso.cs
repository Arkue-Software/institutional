// RedVital — institutional-service
// Celdas de la matriz normativa (DD 15.5, Tabla 37) que tocan este servicio.
// Es la única clase que sabe qué rol hace qué: los casos de uso preguntan,
// no deciden (SRP). APISIX aplica la misma matriz por ruta; aquí se revalida.
//
//   Grupo 10, territorios e instituciones: U4 L, U5 E, U6 E, U7 L
//     (el alta de territorio es solo de U6)
//   Grupo 12, indicadores: U4 L, U5 L, U6 L
//   Grupo 13, bitácora: U7 L
//   Resolver una institución por id: todo perfil con cuenta, fuera de la matriz.

namespace RedVital.Institucional.Dominio;

public static class PoliticaAcceso
{
    public static bool LeeTerritorios(Solicitante s) =>
        s.Es(Roles.AdminBanco, Roles.Coordinador, Roles.AdminNacional, Roles.Auditor);

    public static bool CreaTerritorios(Solicitante s) => s.Es(Roles.AdminNacional);

    public static bool LeeInstituciones(Solicitante s) => LeeTerritorios(s);

    public static bool EscribeInstituciones(Solicitante s) => s.Es(Roles.Coordinador, Roles.AdminNacional);

    public static bool ResuelveInstitucion(Solicitante s) => !s.EsAnonimo;

    public static bool LeeIndicadores(Solicitante s) => s.Es(Roles.AdminBanco, Roles.Coordinador, Roles.AdminNacional);

    public static bool LeeBitacora(Solicitante s) => s.Es(Roles.Auditor);
}
