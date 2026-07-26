using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TaskForge.Domain.Jobs;
using TaskForge.Domain.Workers;

namespace TaskForge.Infrastructure.Persistence;

public sealed class TaskForgeDbContext(DbContextOptions<TaskForgeDbContext> options)
    : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();
    public DbSet<WorkerState> Workers => Set<WorkerState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new JobConfiguration());
        modelBuilder.ApplyConfiguration(new JobAttemptConfiguration());
        modelBuilder.ApplyConfiguration(new WorkerStateConfiguration());
    }

    private sealed class JobConfiguration : IEntityTypeConfiguration<Job>
    {
        public void Configure(EntityTypeBuilder<Job> builder)
        {
            builder.ToTable("Jobs");
            builder.HasKey(job => job.Id);

            builder.Property(job => job.Type)
                .HasMaxLength(100)
                .IsRequired();
            builder.Property(job => job.PayloadJson).IsRequired();
            builder.Property(job => job.Priority)
                .HasConversion<string>()
                .HasMaxLength(20);
            builder.Property(job => job.Status)
                .HasConversion<string>()
                .HasMaxLength(20);
            builder.Property(job => job.OwningWorkerId).HasMaxLength(200);
            builder.Property(job => job.LastError).HasMaxLength(4000);
            builder.Property(job => job.CreatedAtUtc)
                .HasConversion<DateTimeOffsetToBinaryConverter>();
            builder.Property(job => job.UpdatedAtUtc)
                .HasConversion<DateTimeOffsetToBinaryConverter>();
            builder.Property(job => job.QueuedAtUtc)
                .HasConversion<DateTimeOffsetToBinaryConverter>();
            builder.Property(job => job.StartedAtUtc)
                .HasConversion<DateTimeOffsetToBinaryConverter>();
            builder.Property(job => job.CompletedAtUtc)
                .HasConversion<DateTimeOffsetToBinaryConverter>();
            builder.Property(job => job.NextRetryAtUtc)
                .HasConversion<DateTimeOffsetToBinaryConverter>();
            builder.Property(job => job.LeaseExpiresAtUtc)
                .HasConversion<DateTimeOffsetToBinaryConverter>();
            builder.Property(job => job.Version).IsConcurrencyToken();

            builder.HasIndex(job => new { job.Status, job.Priority, job.CreatedAtUtc });
            builder.HasIndex(job => job.NextRetryAtUtc);
            builder.HasIndex(job => job.LeaseExpiresAtUtc);
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
                .HasConversion<DateTimeOffsetToBinaryConverter>();
            builder.Property(attempt => attempt.FinishedAtUtc)
                .HasConversion<DateTimeOffsetToBinaryConverter>();
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
                .HasConversion<DateTimeOffsetToBinaryConverter>();
            builder.Property(worker => worker.LastHeartbeatAtUtc)
                .HasConversion<DateTimeOffsetToBinaryConverter>();
            builder.Property(worker => worker.StoppedAtUtc)
                .HasConversion<DateTimeOffsetToBinaryConverter>();
            builder.Property(worker => worker.Version).IsConcurrencyToken();

            builder.HasIndex(worker => worker.LastHeartbeatAtUtc);
        }
    }
}
