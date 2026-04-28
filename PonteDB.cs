using Microsoft.EntityFrameworkCore;
using ClassesBSFM;
using BSFM.Models;
using System;

namespace PonteBanco
{
    public class PonteDB : DbContext
    {
        // Tabelas do Banco
        public DbSet<Usuario> Usuarios { get; set; }
        public DbSet<Refeição> Refeicoes { get; set; }
        public DbSet<Comida> Comidas { get; set; }
        public DbSet<CronogramaAlimentar> Cronogramas { get; set; }
        public DbSet<Hospital> Hospitais { get; set; }
        public DbSet<AnaliseIA> AnalisesIA { get; set; } // ADICIONADO: Nova tabela
        public DbSet<HistoricoProgresso> Historicos { get; set; }
        
        // Novas tabelas para o módulo de planos alimentares
        public DbSet<CronogramaSemanal> CronogramasSemanais { get; set; }
        public DbSet<RefeicaoDiaria> RefeicoesDiarias { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            var connectionUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

            if (string.IsNullOrEmpty(connectionUrl))
            {
                // Se rodar local sem a variável, usa SQLite
                options.UseSqlite("Data Source=UsuariosBSFM.db");
            }
            else
            {
                // Tenta extrair os dados da URL do Railway/Neon
                try 
                {
                    var databaseUri = new Uri(connectionUrl);
                    var userInfo = databaseUri.UserInfo.Split(':');

                    // CORREÇÃO: Se a porta for -1 (URL sem porta explícita), usa 5432 (padrão PostgreSQL)
                    var port = databaseUri.Port > 0 ? databaseUri.Port : 5432;

                    // Verifica se a URL já contém sslmode (comum no Neon)
                    var sslMode = connectionUrl.Contains("sslmode=require") ? "Require" : "Require";
                    var trustCert = connectionUrl.Contains("sslmode=require") ? "true" : "true";

                    var connectionString = $"Host={databaseUri.Host};" +
                                           $"Port={port};" +
                                           $"Username={userInfo[0]};" +
                                           $"Password={userInfo[1]};" +
                                           $"Database={databaseUri.LocalPath.TrimStart('/')};" +
                                           $"SSL Mode={sslMode};" +
                                           $"Trust Server Certificate={trustCert};" +
                                           "Pooling=true;";

                    options.UseNpgsql(connectionString);
                }
                catch 
                {
                    // Plano de reserva se a URL estiver em formato simples
                    // Remove o -pooler do hostname se presente (Neon)
                    var fallbackString = connectionUrl
                        .Replace("postgres://", "postgresql://")
                        .Replace("-pooler", ""); // Remove pooler que causa problemas
                    options.UseNpgsql(fallbackString);
                }
            }
            
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Ajuste para tabelas com nomes especiais
            modelBuilder.Entity<Refeição>().ToTable("Refeicoes");
            modelBuilder.Entity<ClassesBSFM.AnaliseIA>().ToTable("analises_ia");
            
            // Configuração das novas tabelas
            modelBuilder.Entity<CronogramaSemanal>()
                .HasMany(c => c.RefeicoesDiarias)
                .WithOne(r => r.CronogramaSemanal)
                .HasForeignKey(r => r.CronogramaSemanalId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<RefeicaoDiaria>()
                .HasOne(r => r.CronogramaSemanal)
                .WithMany(c => c.RefeicoesDiarias)
                .HasForeignKey(r => r.CronogramaSemanalId);
        }
    }
}
