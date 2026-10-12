using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// How a schema script becomes batches and what a batch says of itself: the migrations' script
/// splits on its <c>GO</c> lines and nothing else, and a batch knows whether it builds an index,
/// which is what puts it under the host's index timeout.
/// </summary>
public sealed class SchemaBatchesTests
{
    [Fact]
    public void SplitOnGo_ShouldSplitOnGoLinesAloneKeepingOrderAndDroppingBlankBatches()
    {
        // Arrange: Windows line endings, a GO in lower case, one with trailing spaces, a trailing GO
        // with nothing after it, and the word in a statement, which is not a separator.
        const string script = "IF OBJECT_ID(N'[nightingale].[__EFMigrationsHistory]') IS NULL\r\nBEGIN\r\n    CREATE TABLE [nightingale].[__EFMigrationsHistory] (x int);\r\nEND;\r\nGO\r\n\r\ngo  \r\nINSERT INTO t VALUES ('let it GO');\r\nGO\r\nCREATE INDEX ix ON t (x);\r\nGO\r\n";

        // Act
        var batches = SchemaBatches.SplitOnGo(script);

        // Assert
        batches.Count.ShouldBe(3);
        batches[0].ShouldStartWith("IF OBJECT_ID");
        batches[0].ShouldEndWith("END;");
        batches[1].ShouldBe("INSERT INTO t VALUES ('let it GO');");
        batches[2].ShouldBe("CREATE INDEX ix ON t (x);");
    }

    [Theory]
    [InlineData("CREATE INDEX ix_pc_events_category_seq ON dbo.pc_events (tenant_id, category, seq_id) WHERE (is_archived = 0);", true)]
    [InlineData("CREATE UNIQUE INDEX ix_pc_events_stream_and_version ON dbo.pc_events (tenant_id, stream_id, version);", true)]
    [InlineData("create unique nonclustered index ix on t (x);", true)]
    [InlineData("CREATE CLUSTERED INDEX ix ON t (x);", true)]
    [InlineData("ALTER TABLE dbo.pc_events ADD category_ordinal bigint NULL;\nALTER INDEX ix_pc_events_type_seq ON dbo.pc_events REBUILD;", true)]
    [InlineData("IF OBJECT_ID('dbo.pc_events') IS NULL\nBEGIN\nCREATE TABLE dbo.pc_events (seq_id bigint);\nEND\nCREATE INDEX ix ON dbo.pc_events (seq_id);", true)]
    [InlineData("ALTER TABLE dbo.pc_events ADD category_ordinal bigint NULL;", false)]
    [InlineData("CREATE TABLE dbo.index_log (id int);", false)]
    [InlineData("DROP INDEX ix_pc_events_category_seq ON dbo.pc_events;", false)]
    [InlineData("INSERT INTO [nightingale].[__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'20261007042227_Initial', N'10.0.0');", false)]
    public void BuildsAnIndex_ShouldSayWhetherABatchCreatesOrAltersAnIndex(string batch, bool expected)
    {
        // Act & Assert
        SchemaBatches.BuildsAnIndex(batch).ShouldBe(expected);
    }

    [Fact]
    public void FirstLineOf_ShouldBeTheFirstLineThatSaysAnything()
    {
        // Act & Assert
        SchemaBatches.FirstLineOf("\r\n   \r\n  CREATE INDEX ix ON t (x)  \r\nWHERE (x > 0);").ShouldBe("CREATE INDEX ix ON t (x)");
        SchemaBatches.FirstLineOf("  ").ShouldBe(string.Empty);
    }
}
