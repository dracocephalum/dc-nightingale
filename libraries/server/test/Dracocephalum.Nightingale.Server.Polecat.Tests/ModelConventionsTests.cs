using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The check on how a column that refers to another table is named and declared, on models
/// small enough to break one rule at a time. That the gateway's own model passes is
/// <see cref="NightingaleDbContextTests"/>'s.
/// </summary>
public sealed class ModelConventionsTests
{
    private const string DesignTime = "Server=localhost;Database=design-time;Integrated Security=true";

    [Fact]
    public void Check_WhenAColumnIsNamedForATableOfTheModelAndIsAForeignKeyToIt_ShouldPass()
    {
        // Arrange
        using var context = Context(model => model.Entity<Order>().HasOne<Customer>().WithMany().HasForeignKey(order => order.CustomerId).OnDelete(DeleteBehavior.Restrict));

        // Act & Assert
        Should.NotThrow(() => ModelConventions.Check(context.Model, "Order.EventId"));
    }

    [Fact]
    public void Check_WhenAColumnIsNamedForATableOfTheModelAndIsNotAForeignKey_ShouldSaySo()
    {
        // Arrange: the name promises a reference the database does not hold.
        using var context = Context(_ => { });

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => ModelConventions.Check(context.Model, "Order.EventId"));

        // Assert
        exception.Message.ShouldContain("Order.CustomerId: refers to Customer by name and is not a foreign key to it");
    }

    [Fact]
    public void Check_WhenAColumnRefersOutsideTheModelAndIsNotListed_ShouldSaySo()
    {
        // Arrange
        using var context = Context(model => model.Entity<Order>().HasOne<Customer>().WithMany().HasForeignKey(order => order.CustomerId).OnDelete(DeleteBehavior.Restrict));

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => ModelConventions.Check(context.Model));

        // Assert
        exception.Message.ShouldContain("Order.EventId: named like a reference to a table 'Event' this model does not have");
    }

    [Fact]
    public void Check_WhenAForeignKeyIsNotNamedForItsTable_ShouldSaySo()
    {
        // Arrange: a real foreign key under a name that does not say where it points.
        using var context = Context(model =>
        {
            model.Entity<Order>().HasOne<Customer>().WithMany().HasForeignKey(order => order.CustomerId).OnDelete(DeleteBehavior.Restrict);
            model.Entity<Order>().HasOne<Customer>().WithMany().HasForeignKey(order => order.BillTo).OnDelete(DeleteBehavior.Restrict);
        });

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => ModelConventions.Check(context.Model, "Order.EventId"));

        // Assert
        exception.Message.ShouldContain("the foreign key column must be 'CustomerId'");
    }

    [Fact]
    public void Check_WhenAForeignKeyCascades_ShouldSaySo()
    {
        // Arrange
        using var context = Context(model => model.Entity<Order>().HasOne<Customer>().WithMany().HasForeignKey(order => order.CustomerId).OnDelete(DeleteBehavior.Cascade));

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => ModelConventions.Check(context.Model, "Order.EventId"));

        // Assert
        exception.Message.ShouldContain("Cascade");
    }

    [Fact]
    public void CheckSets_WhenATableHasNoSetOrASetIsNotNamedForItsEntity_ShouldSaySo()
    {
        // Arrange: Customer is exposed under another name, and Order not at all.
        using var context = Context(model => model.Entity<Order>().HasOne<Customer>().WithMany().HasForeignKey(order => order.CustomerId).OnDelete(DeleteBehavior.Restrict));

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => ModelConventions.CheckSets(context));

        // Assert
        exception.Message.ShouldContain("Order: the context has no DbSet<Order>");
        exception.Message.ShouldContain("Shop.Buyers: a set is named for its entity - 'Customer' or its plural");
    }

    [Theory]
    [InlineData(typeof(Named<Customer>))]
    [InlineData(typeof(Plural<Customer>))]
    public void CheckSets_WhenASetIsItsEntitysNameOrPlural_ShouldPass(Type contextType)
    {
        // Arrange
        using var context = (DbContext)Activator.CreateInstance(contextType)!;

        // Act & Assert
        Should.NotThrow(() => ModelConventions.CheckSets(context));
    }

    [Fact]
    public void Check_WhenAnEnumThatStatesItsNumbersAreStoredIsStoredByThem_ShouldPass()
    {
        // Arrange
        using var context = Context(model =>
        {
            Related(model);
            model.Entity<Ticket>().ToTable(nameof(Ticket)).Property(ticket => ticket.Priority).HasConversion<byte>();
            model.Entity<Ticket>().Property(ticket => ticket.Status).HasConversion<string>().HasMaxLength(64);
        });

        // Act & Assert
        Should.NotThrow(() => ModelConventions.Check(context.Model, "Order.EventId"));
    }

    [Fact]
    public void Check_WhenAnEnumIsStoredByNumberWithoutStatingIt_ShouldSaySo()
    {
        // Arrange
        using var context = Context(model =>
        {
            Related(model);
            model.Entity<Ticket>().ToTable(nameof(Ticket)).Property(ticket => ticket.Priority).HasConversion<byte>();
        });

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => ModelConventions.Check(context.Model, "Order.EventId"));

        // Assert: the enum with no statement is held to the rule, the one with it is not.
        exception.Message.ShouldContain("Ticket.Status: enum must be a bounded string");
        exception.Message.ShouldNotContain("Ticket.Priority");
    }

    [Fact]
    public void Check_WhenAnEnumThatStatesItsNumbersAreStoredIsStoredWider_ShouldSaySo()
    {
        // Arrange: a byte's numbers in an int column, wider than the enum's own type.
        using var context = Context(model =>
        {
            Related(model);
            model.Entity<Ticket>().ToTable(nameof(Ticket)).Property(ticket => ticket.Priority).HasConversion<int>();
            model.Entity<Ticket>().Property(ticket => ticket.Status).HasConversion<string>().HasMaxLength(64);
        });

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => ModelConventions.Check(context.Model, "Order.EventId"));

        // Assert
        exception.Message.ShouldContain("Ticket.Priority: an enum stored as a number is stored as its underlying type - .HasConversion<Byte>()");
    }

    [Fact]
    public void Check_WhenATableStatesItExtendsTheStoresAndIsKeyedByWhatItDescribes_ShouldPass()
    {
        // Arrange
        using var context = Context(model =>
        {
            Related(model);
            model.Entity<Annotation>().ToTable(nameof(Annotation)).HasKey(row => new { row.SeqId, row.Note });
        });

        // Act & Assert
        Should.NotThrow(() => ModelConventions.Check(context.Model, "Order.EventId"));
    }

    [Fact]
    public void Check_WhenATableIsKeyedByItsColumnsWithoutStatingItExtendsTheStores_ShouldSaySo()
    {
        // Arrange
        using var context = Context(model =>
        {
            Related(model);
            model.Entity<Remark>().ToTable(nameof(Remark)).HasKey(row => new { row.SeqId, row.Note });
        });

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => ModelConventions.Check(context.Model, "Order.EventId"));

        // Assert
        exception.Message.ShouldContain("Remark: primary key must be a single Guid column named Id");
    }

    private static Shop Context(Action<ModelBuilder> relationships) =>
        new(new DbContextOptionsBuilder<Shop>().UseSqlServer(DesignTime).Options, relationships);

    private static void Related(ModelBuilder model) =>
        model.Entity<Order>().HasOne<Customer>().WithMany().HasForeignKey(order => order.CustomerId).OnDelete(DeleteBehavior.Restrict);

    private sealed class Customer
    {
        public Guid Id { get; set; }
    }

    private sealed class Order
    {
        public Guid Id { get; set; }

        public Guid CustomerId { get; set; }

        public Guid BillTo { get; set; }

        public Guid EventId { get; set; }
    }

    /// <summary>An enum whose numbers a column stores, as it states.</summary>
    [StoredAsNumber]
    private enum Priority : byte
    {
        Low = 1,
        High = 2,
    }

    /// <summary>An enum stored by name, as the rule has it.</summary>
    private enum Status
    {
        Open = 0,
        Closed = 1,
    }

    /// <summary>A table that extends the store's, keyed by what it describes, as it states.</summary>
    [StoreExtension]
    private sealed class Annotation
    {
        public long SeqId { get; set; }

        public string Note { get; set; } = string.Empty;
    }

    /// <summary>The same table, without the statement.</summary>
    private sealed class Remark
    {
        public long SeqId { get; set; }

        public string Note { get; set; } = string.Empty;
    }

    private sealed class Ticket
    {
        public Guid Id { get; set; }

        public Priority Priority { get; set; }

        public Status Status { get; set; }
    }

    /// <summary>A context whose one set has its entity's own name, as a word with no plural would.</summary>
    private sealed class Named<T> : DbContext
        where T : class
    {
        public DbSet<Customer> Customer => Set<Customer>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlServer(DesignTime);
    }

    /// <summary>A context whose one set is its entity's plural.</summary>
    private sealed class Plural<T> : DbContext
        where T : class
    {
        public DbSet<Customer> Customers => Set<Customer>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlServer(DesignTime);
    }

    /// <summary>A model the test shapes, cached per instance so each test builds its own.</summary>
    private sealed class Shop(DbContextOptions<Shop> options, Action<ModelBuilder> relationships) : DbContext(options)
    {
        public DbSet<Customer> Buyers => Set<Customer>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.EnableServiceProviderCaching(false);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>().ToTable(nameof(Customer));
            modelBuilder.Entity<Order>().ToTable(nameof(Order));
            relationships(modelBuilder);
        }
    }
}
