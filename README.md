# Servicio Institucional (RedVital)

Servicio de .NET 10 dueño de `db_institucional` (DD V3.0, Sección 7). Tiene tres responsabilidades:

| Módulo | Qué hace | Requisitos |
|---|---|---|
| M6 | Jerarquía territorial (nacional, departamental, municipal) y registro de instituciones | RF-09, EC-39, EC-42, IN-13 |
| M7 | Indicadores de captación, existencias y cobertura, en solo lectura | RF-13, RI-03 |
| ST2 | Consulta de la bitácora: compone las cuatro series de auditoría | RF-21, RI-04, ADR-016 |

Ningún servicio le hace llamadas y él no llama a ninguno: con los demás solo habla por Kafka. Publica cada alta o cambio de institución y compone indicadores y bitácora con lo que recibe.

## Cómo se conecta

```
Web ── /api/v1/{territorios,instituciones,indicadores,auditoria} ──► APISIX ──► Institucional ──► db_institucional

Institucional ── EV-07 institution.changed ──► Kafka ──► Campañas, Donación   (copia local de las instituciones)
Donación ── EV-09 indicators.updated ──► Kafka ──► Institucional             (fotografía de M7 por institución)
Identidad, Campañas, Donación ── EV-10 audit.recorded ──► Kafka ──► Institucional   (las tres series de la bitácora)
```

- **EV-07** sale de la bandeja de salida (`evento_salida`) en la misma transacción que el alta o el cambio, con el estado completo de la institución. El tema está compactado y no vence: un consumidor nuevo lo lee desde el principio y queda con todas.
- **EV-09** reemplaza la fotografía de la institución en `captacion_diaria` y `existencia_agregada`. Una fotografía más vieja que la vigente no la reemplaza.
- **EV-10** se anexa a `registro_bitacora`, con su contexto. La clave es `(contexto, id)`: un reenvío no duplica.
- Cada evento aplicado queda en `evento_procesado`. Un mensaje que no cumple el contrato va a `<tema>.institucional.dlt`; uno que falla se reintenta 3 veces (1, 2 y 4 s) y luego va a mensajes muertos. Si la base no responde, el evento espera sin ir a mensajes muertos.

Cuando un servicio no está:

| Falla | Efecto |
|---|---|
| Donación | Los indicadores responden con la última fotografía recibida y dicen de cuándo es (`actualizado_en`). La bitácora y M6 siguen igual. |
| Campañas o Identidad | Sus registros esperan en su bandeja y llegan a la bitácora cuando vuelven. |
| Institucional | Campañas crea campañas y Donación resuelve el alcance territorial con su copia de las instituciones. Lo que pasa mientras tanto llega a la bitácora cuando vuelve. |
| Kafka | Todo sigue atendiendo. Los eventos esperan en las bandejas de salida y se entregan cuando el broker vuelve. |

## Operaciones

| Operación | Quién (DD 15.5) | Notas |
|---|---|---|
| `GET /v1/territorios` | U4 a U7 | Acotado a la jurisdicción: el país, el departamento o el municipio del banco. Filtros `nivel`, `padre_id`, `pagina`, `tamano`. |
| `POST /v1/territorios` | U6 | Cuerpo `{codigo_divipola, nombre}`. El nivel, el padre y la ruta salen del código. |
| `GET /v1/instituciones` | U4 a U7 | `territorio_id` o `territorio_ruta`, `incluir_descendientes` (verdadero por defecto), `tipo`, `incluir_bajas` (U5 y U6), paginación. Fuera de la jurisdicción: 403 `fuera-de-jurisdiccion`, auditado. |
| `GET /v1/instituciones/{id}` | Toda cuenta | `id, codigo, nombre, tipo, territorio_id, territorio_codigo, territorio_ruta, territorio_nombre, direccion, vigente, creado_en`. |
| `POST /v1/instituciones` | U5 en su jurisdicción, U6 | Nace vigente en un municipio; el momento del alta lo fija la base (EC-42). Publica EV-07. |
| `PUT /v1/instituciones/{id}` | U5 en su jurisdicción, U6 | `nombre`, `direccion`, `vigente`. El tipo no cambia (409). Fuera de la jurisdicción responde 404, auditado. Publica EV-07. |
| `GET /v1/indicadores` | U4, U5, U6 | `desde`, `hasta` (hasta dos años), `agrupar_por` (`dia`, `semana`, `mes`), `institucion_id` o `territorio_ruta`. Responde `actualizado_en`: la fotografía más antigua de las instituciones medidas, o nulo si ninguna ha llegado. Con menos de cinco donaciones en un periodo, `unidades_no_aptas` va nulo y `suprimido_por_agregacion` en verdadero. |
| `GET /v1/auditoria` | U7 | Filtros `recurso_tipo`, `recurso_id`, `correlacion_id`, `actor_id`, `desde`, `hasta`, `tamano` (hasta 200) y el cursor `antes_de`. Responde `registros`, `siguiente` y `series`: hasta cuándo está al día cada una (`al_dia_hasta`, el último registro recibido de ese contexto). |

Errores con la forma común RFC 9457: `{tipo, titulo, estado, detalle, correlacion_id}`. Una propiedad desconocida en el cuerpo es un 400.

Los contratos de los eventos están en [`contratos/asyncapi.yaml`](contratos/asyncapi.yaml).

## Diseño

Arquitectura hexagonal, con la regla de dependencias comprobada por `ArquitecturaPruebas`:

| Proyecto | Contenido | Depende de |
|---|---|---|
| `Dominio` | `RutaTerritorial` (IN-13), `Territorio`, `Institucion`, `Solicitante`, `PoliticaAcceso` (la matriz 15.5), `RegistroAuditoria` | Nada |
| `Aplicacion` | Un caso de uso por clase y los puertos que necesita cada uno | Dominio |
| `Infraestructura` | EF Core sobre PostgreSQL: repositorios, bandeja de salida y copias de EV-09 y EV-10 | Aplicacion |
| `Api` | Controladores, seguridad, Kafka (publicador y consumidores) y composición | Todo |

- **Responsabilidad única:** cada caso de uso hace una cosa; la matriz de acceso vive en una sola clase.
- **Abierto/cerrado:** la bitácora compone cualquier `IFuenteAuditoria`. Agregar una serie es registrar otra fuente; cada consumidor de Kafka es una subclase de un ciclo común.
- **Sustitución:** la serie propia y las copias de las demás cumplen el mismo contrato; el compositor no las distingue.
- **Segregación de interfaces:** un puerto por necesidad (territorios, instituciones, cobertura, auditoría, indicadores, bandeja, proyecciones).
- **Inversión de dependencias:** los casos de uso dependen de puertos; la base y Kafka entran por adaptadores.

## Configuración

| Clave | Para qué |
|---|---|
| `ConnectionStrings__Institucional` | Cadena de `db_institucional` como `institucional_servicio`, sin contraseña |
| `/run/secrets/institucional_servicio` | Contraseña de la base |
| `Jwt__LlavePublica` | Llave pública de firma de Identidad; por defecto `/run/secrets/identidad_llave_publica` |
| `Kafka__Habilitado`, `Kafka__Servidores`, `Kafka__Usuario` | Conexión al broker, con el usuario SCRAM `institucional` |
| `/run/secrets/kafka_institucional` | Contraseña SCRAM |

## Pruebas

```bash
dotnet test RedVital.Institucional.sln
```

Dominio, casos de uso con dobles, arquitectura, la API completa y los adaptadores sobre PostgreSQL real, y la mensajería contra Kafka real (Testcontainers), con las migraciones del repo de bases. En este equipo se corren con `Despliegue_RedVital/pruebas/correr-pruebas.sh institucional`.
