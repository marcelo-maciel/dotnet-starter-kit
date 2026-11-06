using FSH.Framework.Core.Context;
using FSH.Framework.Core.Domain;
using FSH.Framework.Persistence.Inteceptors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Framework.Tests.Persistence;

/// <summary>
/// Soft delete of an entity that owns a value object. EF cascades Deleted onto the owned reference; the interceptor
/// turns the owner into an UPDATE and must put the owned entry back to Unchanged, otherwise the UPDATE writes NULL into
/// the owned columns (the Product.Price/Money NOT NULL violation the interceptor's comment records).
/// </summary>
public sealed class AuditableEntitySoftDeleteOwnedTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SoftDelete_WithOwnedReference_KeepsOwnedColumnsAndMarksDeleted()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var userId = Guid.NewGuid();
        var id = Guid.NewGuid();

        await using (var seed = NewContext(connection, userId))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Items.Add(new PricedItem { Id = id, Price = new Money { Amount = 12.34m, Currency = "BRL" } });
            await seed.SaveChangesAsync();
        }

        await using (var delete = NewContext(connection, userId))
        {
            delete.Items.Remove(await delete.Items.SingleAsync(i => i.Id == id));
            await delete.SaveChangesAsync();
        }

        await using var read = NewContext(connection, userId);
        var row = await read.Items.AsNoTracking().SingleAsync(i => i.Id == id);
        row.IsDeleted.ShouldBeTrue();
        row.DeletedOnUtc.ShouldBe(Agora);
        row.DeletedBy.ShouldBe(userId.ToString());
        row.Price.Amount.ShouldBe(12.34m);
        row.Price.Currency.ShouldBe("BRL");
    }

    private static SoftDeleteContext NewContext(SqliteConnection connection, Guid userId)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.IsAuthenticated().Returns(true);
        currentUser.GetUserId().Returns(userId);
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(Agora);

        var options = new DbContextOptionsBuilder<SoftDeleteContext>()
            .UseSqlite(connection)
            .AddInterceptors(new AuditableEntitySaveChangesInterceptor(currentUser, clock))
            .Options;
        return new SoftDeleteContext(options);
    }

    private sealed class SoftDeleteContext(DbContextOptions<SoftDeleteContext> options) : DbContext(options)
    {
        public DbSet<PricedItem> Items => Set<PricedItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PricedItem>(b =>
            {
                b.HasKey(i => i.Id);
                b.OwnsOne(i => i.Price, p =>
                {
                    p.Property(m => m.Amount).IsRequired();
                    p.Property(m => m.Currency).IsRequired();
                });
                b.Navigation(i => i.Price).IsRequired();
            });
        }
    }

    private sealed class PricedItem : ISoftDeletable
    {
        public Guid Id { get; set; }

        public Money Price { get; set; } = default!;

        public bool IsDeleted { get; set; }

        public DateTimeOffset? DeletedOnUtc { get; set; }

        public string? DeletedBy { get; set; }
    }

    private sealed class Money
    {
        public decimal Amount { get; set; }

        public string Currency { get; set; } = string.Empty;
    }
}
