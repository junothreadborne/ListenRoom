using Microsoft.EntityFrameworkCore;
using ListenRoom.Web.Models;

namespace ListenRoom.Web.Data;

public class AppDbContext : DbContext
{
    public DbSet<Session> Sessions { get; set; }
    public DbSet<Participant> Participants { get; set; }
    public DbSet<RecordingChunk> RecordingChunks { get; set; }
    public DbSet<AssembledRecording> AssembledRecordings { get; set; }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Session>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Status).HasConversion<string>();
            entity.HasMany(s => s.Participants)
                .WithOne(p => p.Session)
                .HasForeignKey(p => p.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(s => s.RecordingChunks)
                .WithOne(c => c.Session)
                .HasForeignKey(c => c.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Participant>(entity =>
        {
            entity.HasKey(p => p.Id);
        });

        modelBuilder.Entity<RecordingChunk>(entity =>
        {
            entity.HasKey(c => c.Id);
        });

        modelBuilder.Entity<AssembledRecording>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasOne(r => r.Session)
                .WithMany(s => s.AssembledRecordings)
                .HasForeignKey(r => r.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
