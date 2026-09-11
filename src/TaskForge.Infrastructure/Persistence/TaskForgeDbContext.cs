using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TaskForge.Domain.Jobs;
using TaskForge.Domain.Workers;

namespace TaskForge.Infrastructure.Persistence;

public sealed class TaskForgeDbContext(DbContextOptions<TaskForgeDbContext> options)
    : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();
    public DbSet<WorkerState> Workers => Set<WorkerState>();
    internal DbSet<WorkerSettingsRecord> WorkerSettings =>
        Set<WorkerSettingsRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new JobConfiguration());
        modelBuilder.ApplyConfiguration(new JobAttemptConfiguration());
        modelBuilder.ApplyConfiguration(new WorkerStateConfiguration());
        modelBuilder.ApplyConfiguration(new WorkerSettingsConfiguration());
    }

    private sealed class JobConfiguration : IEntityTypeConfiguration<Job>
    {
        public void Configure(EntityTypeBuilder<Job> builder)
        {
            builder.ToTable("Jobs");
            builder.HasKey(job => job.Id);

            builder.Property(job => job.Type)
                .HasMaxLength(100)
                .UseCollation("Latin1_General_100_CI_AS")
                .IsRequired();
            builder.Property(job => job.PayloadJson).IsRequired();
            builder.Property(job => job.IdempotencyKey)
                .HasMaxLength(100)
                .UseCollation("Latin1_General_100_BIN2");
            builder.Property(job => job.ResultJson).HasMaxLength(4000);
            builder.Property(job => job.Priority);
            builder.Property(job => job.Status)
                .HasConversion<string>()
                .HasMaxLength(20);
            builder.Property(job => job.OwningWorkerId).HasMaxLength(200);
            builder.Property(job => job.LastError).HasMaxLength(4000);
            builder.Property(job => job.CreatedAtUtc)
                .HasColumnType("datetimeoffset");
            builder.Property(job => job.UpdatedAtUtc)
                .HasColumnType("datetimeoffset");
            builder.Property(job => job.QueuedAtUtc)
                .HasColumnType("datetimeoffset");
            builder.Property(job => job.StartedAtUtc)
                .HasColumnType("datetimeoffset");
            builder.Property(job => job.CompletedAtUtc)
                .HasColumnType("datetimeoffset");
            builder.Property(job => job.NextRetryAtUtc)
                .HasColumnType("datetimeoffset");
            builder.Property(job => job.LeaseExpiresAtUtc)
                .HasColumnType("datetimeoffset");
            builder.Property(job => job.Version).IsConcurrencyToken();

            builder.HasIndex(job => new { job.Status, job.Priority, job.CreatedAtUtc });
            builder.HasIndex(job => job.NextRetryAtUtc);
            builder.HasIndex(job => job.LeaseExpiresAtUtc);
            builder.HasIndex(job => job.IdempotencyKey).IsUnique();
        }
    }

    private sealed class JobAttemptConfiguration : IEntityTypeConfiguration<JobAttempt>
    {
        public void Configure(EntityTypeBuilder<JobAttempt> builder)
        {
            builder.ToTable("JobAttempts");
            builder.HasKey(attempt => attempt.Id);

            builder.Property(attempt => attempt.WorkerId)
                .HasMaxLength(200)
                .IsRequired();
            builder.Property(attempt => attempt.Outcome)
                .HasConversion<string>()
                .HasMaxLength(20);
            builder.Property(attempt => attempt.StartedAtUtc)
                .HasColumnType("datetimeoffset");
            builder.Property(attempt => attempt.FinishedAtUtc)
                .HasColumnType("datetimeoffset");
            builder.Property(attempt => attempt.ErrorCode).HasMaxLength(100);
            builder.Property(attempt => attempt.ErrorMessage).HasMaxLength(4000);

            builder.HasIndex(attempt => new { attempt.JobId, attempt.AttemptNumber })
                .IsUnique();
            builder.HasOne<Job>()
                .WithMany()
                .HasForeignKey(attempt => attempt.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }

    private sealed class WorkerStateConfiguration : IEntityTypeConfiguration<WorkerState>
    {
        public void Configure(EntityTypeBuilder<WorkerState> builder)
        {
            builder.ToTable("Workers");
            builder.HasKey(worker => worker.Id);

            builder.Property(worker => worker.Id).HasMaxLength(200);
            builder.Property(worker => worker.Status)
                .HasConversion<string>()
                .HasMaxLength(20);
            builder.Property(worker => worker.StartedAtUtc)
                .HasColumnType("datetimeoffset");
            builder.Property(worker => worker.LastHeartbeatAtUtc)
                .HasColumnType("datetimeoffset");
            builder.Property(worker => worker.StoppedAtUtc)
                .HasColumnType("datetimeoffset");
            builder.Property(worker => worker.Version).IsConcurrencyToken();

            builder.HasIndex(worker => worker.LastHeartbeatAtUtc);
        }
    }

    private sealed class WorkerSettingsConfiguration
        : IEntityTypeConfiguration<WorkerSettingsRecord>
    {
        public void Configure(EntityTypeBuilder<WorkerSettingsRecord> builder)
        {
            builder.ToTable("WorkerSettings");
            builder.HasKey(settings => settings.Id);
            builder.Property(settings => settings.Id).ValueGeneratedNever();
            builder.Property(settings => settings.UpdatedAtUtc)
                .HasColumnType("datetimeoffset");
        }
    }
}
