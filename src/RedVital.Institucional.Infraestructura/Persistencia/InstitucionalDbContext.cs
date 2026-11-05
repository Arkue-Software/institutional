// RedVital — institutional-service. Mapeo de db_institucional (migraciones en
// BDs_RedVital/databases/institucional). El servicio se conecta como
// institucional_servicio, que solo tiene los permisos por columna de V001–V004:
// por eso lo que la base calcula (vigente al nacer, creado_en) no se envía.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using RedVital.Institucional.Dominio;

namespace RedVital.Institucional.Infraestructura.Persistencia;

public sealed class FilaAuditoria
{
    public Guid Id { get; set; }
    public string ActorTipo { get; set; } = "";
    public string? ActorId { get; set; }
    public string? Rol { get; set; }
    public string? JurisdiccionSolicitada { get; set; }
    public string Operacion { get; set; } = "";
    public string RecursoTipo { get; set; } = "";
    public Guid? RecursoId { get; set; }
    public string Resultado { get; set; } = "";
    public string CorrelacionId { get; set; } = "";
    public string? Origen { get; set; }
    public DateTimeOffset OcurridoEn { get; set; }

    public RegistroAuditoria ComoRegistro() => new(Id, ActorTipo, ActorId, Rol, JurisdiccionSolicitada, Operacion,
        RecursoTipo, RecursoId, Resultado, CorrelacionId, Origen, OcurridoEn);
}

public sealed class InstitucionalDbContext(DbContextOptions<InstitucionalDbContext> opciones) : DbContext(opciones)
{
    public DbSet<Territorio> Territorios => Set<Territorio>();
    public DbSet<Institucion> Instituciones => Set<Institucion>();
    public DbSet<FilaAuditoria> Auditoria => Set<FilaAuditoria>();
    public DbSet<FilaEventoSalida> EventosSalida => Set<FilaEventoSalida>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Territorio>(e =>
        {
            e.ToTable("territorio");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(t => t.CodigoDivipola).HasColumnName("codigo_divipola");
            e.Property(t => t.Nombre).HasColumnName("nombre");
            e.Property(t => t.Nivel).HasColumnName("nivel")
                .HasConversion(v => Niveles.Codigo(v), v => Niveles.Desde(v)!.Value);
            e.Property(t => t.PadreId).HasColumnName("padre_id");
            e.Property(t => t.Ruta).HasColumnName("ruta");
            NoSeEnviaAlCrear(e.Property(t => t.Vigente).HasColumnName("vigente"));
            e.Ignore(t => t.RutaTerritorial);
        });
        m.Entity<Institucion>(e =>
        {
            e.ToTable("institucion");
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(i => i.Codigo).HasColumnName("codigo");
            e.Property(i => i.Nombre).HasColumnName("nombre");
            e.Property(i => i.Tipo).HasColumnName("tipo")
                .HasConversion(v => Tipos.Codigo(v), v => Tipos.Desde(v)!.Value);
            e.Property(i => i.TerritorioId).HasColumnName("territorio_id");
            e.Property(i => i.Direccion).HasColumnName("direccion");
            NoSeEnviaAlCrear(e.Property(i => i.Vigente).HasColumnName("vigente"));
            // EC-42: el momento del alta lo fija la base, y nadie lo reescribe.
            var creado = e.Property(i => i.CreadoEn).HasColumnName("creado_en");
            NoSeEnviaAlCrear(creado);
            creado.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
            e.Property(i => i.CreadoPor).HasColumnName("creado_por");
        });
        m.Entity<FilaAuditoria>(e =>
        {
            e.ToTable("registro_auditoria_inst");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(r => r.ActorTipo).HasColumnName("actor_tipo");
            e.Property(r => r.ActorId).HasColumnName("actor_id");
            e.Property(r => r.Rol).HasColumnName("rol");
            e.Property(r => r.JurisdiccionSolicitada).HasColumnName("jurisdiccion_solicitada");
            e.Property(r => r.Operacion).HasColumnName("operacion");
            e.Property(r => r.RecursoTipo).HasColumnName("recurso_tipo");
            e.Property(r => r.RecursoId).HasColumnName("recurso_id");
            e.Property(r => r.Resultado).HasColumnName("resultado");
            e.Property(r => r.CorrelacionId).HasColumnName("correlacion_id");
            e.Property(r => r.Origen).HasColumnName("origen");
            e.Property(r => r.OcurridoEn).HasColumnName("ocurrido_en");
        });
        m.Entity<FilaEventoSalida>(e =>
        {
            e.ToTable("evento_salida");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            // La genera la base (identidad): fija el orden de publicación y nunca sale del servicio.
            e.Property(x => x.Secuencia).HasColumnName("secuencia").ValueGeneratedOnAdd();
            e.Property(x => x.Tipo).HasColumnName("tipo");
            e.Property(x => x.VersionEsquema).HasColumnName("version_esquema");
            e.Property(x => x.Tema).HasColumnName("tema");
            e.Property(x => x.Clave).HasColumnName("clave");
            e.Property(x => x.Carga).HasColumnName("carga").HasColumnType("jsonb");
            e.Property(x => x.ClaveNatural).HasColumnName("clave_natural");
            e.Property(x => x.CorrelacionId).HasColumnName("correlacion_id");
            e.Property(x => x.Estado).HasColumnName("estado");
            e.Property(x => x.IntentosPublicacion).HasColumnName("intentos_publicacion");
            e.Property(x => x.CreadoEn).HasColumnName("creado_en");
            e.Property(x => x.PublicadoEn).HasColumnName("publicado_en");
            e.Property(x => x.ExpiraEn).HasColumnName("expira_en");
        });
    }

    /// <summary>La base pone el valor por defecto en el INSERT; EF lo lee de vuelta.</summary>
    private static void NoSeEnviaAlCrear<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<T> p)
    {
        p.ValueGeneratedOnAdd();
        p.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
