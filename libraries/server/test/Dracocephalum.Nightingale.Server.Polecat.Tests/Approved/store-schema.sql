SET QUOTED_IDENTIFIER ON;


IF NOT EXISTS ( SELECT  *
                FROM    sys.schemas
                WHERE   name = N'dbo' )
    EXEC('CREATE SCHEMA [dbo]');


IF OBJECT_ID('dbo.pc_streams') IS NULL
BEGIN
CREATE TABLE dbo.pc_streams (
    tenant_id            varchar(250)      NOT NULL,
    id                   varchar(250)      NOT NULL,
    type                 varchar(250)      NULL,
    version              bigint            NOT NULL DEFAULT 0,
    timestamp            datetimeoffset    NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    created              datetimeoffset    NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    is_archived          bit               NOT NULL DEFAULT 0,
    compacted_version    bigint            NOT NULL DEFAULT 0,
CONSTRAINT pkey_pc_streams_tenant_id_id PRIMARY KEY (tenant_id, id)
);
END
IF OBJECT_ID('dbo.pc_events') IS NULL
BEGIN
CREATE TABLE dbo.pc_events (
    seq_id            bigint              NOT NULL IDENTITY,
    id                uniqueidentifier    NOT NULL,
    stream_id         varchar(250)        NOT NULL,
    version           bigint              NOT NULL,
    data              json                NOT NULL,
    bdata             varbinary(max)      NULL,
    type              varchar(500)        NOT NULL,
    timestamp         datetimeoffset      NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    tenant_id         varchar(250)        NOT NULL DEFAULT '*DEFAULT*',
    dotnet_type       varchar(500)        NULL,
    correlation_id    varchar(250)        NULL,
    causation_id      varchar(250)        NULL,
    headers           json                NULL,
    is_archived       bit                 NOT NULL DEFAULT 0,
    category AS (CASE WHEN CHARINDEX('-', stream_id) > 1 THEN LEFT(stream_id, CHARINDEX('-', stream_id) - 1) ELSE stream_id END) PERSISTED,
CONSTRAINT pkey_pc_events_seq_id PRIMARY KEY (seq_id)
);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_pc_events_stream_and_version' AND object_id = OBJECT_ID(N'dbo.pc_events'))
    CREATE UNIQUE INDEX ix_pc_events_stream_and_version ON dbo.pc_events (tenant_id, stream_id, version);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_pc_events_category_seq' AND object_id = OBJECT_ID(N'dbo.pc_events'))
    CREATE INDEX ix_pc_events_category_seq ON dbo.pc_events (tenant_id, category, seq_id) WHERE (is_archived = 0);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_pc_events_type_seq' AND object_id = OBJECT_ID(N'dbo.pc_events'))
    CREATE INDEX ix_pc_events_type_seq ON dbo.pc_events (tenant_id, type, seq_id) WHERE (is_archived = 0);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_pc_events_correlation_seq' AND object_id = OBJECT_ID(N'dbo.pc_events'))
    CREATE INDEX ix_pc_events_correlation_seq ON dbo.pc_events (tenant_id, correlation_id, seq_id) WHERE (correlation_id IS NOT NULL AND is_archived = 0);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_pc_events_stream_starts' AND object_id = OBJECT_ID(N'dbo.pc_events'))
    CREATE INDEX ix_pc_events_stream_starts ON dbo.pc_events (tenant_id, seq_id) WHERE (version = 1 AND is_archived = 0);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_pc_events_category_starts' AND object_id = OBJECT_ID(N'dbo.pc_events'))
    CREATE INDEX ix_pc_events_category_starts ON dbo.pc_events (tenant_id, category, seq_id) WHERE (version = 1 AND is_archived = 0);
IF OBJECT_ID('dbo.pc_event_progression') IS NULL
BEGIN
CREATE TABLE dbo.pc_event_progression (
    name            varchar(200)      NOT NULL,
    last_seq_id     bigint            NOT NULL DEFAULT 0,
    last_updated    datetimeoffset    NOT NULL DEFAULT SYSDATETIMEOFFSET(),
CONSTRAINT pkey_pc_event_progression_name PRIMARY KEY (name)
);
END
IF OBJECT_ID('dbo.pc_doc_deadletterevent') IS NULL
BEGIN
CREATE TABLE dbo.pc_doc_deadletterevent (
    tenant_id        varchar(250)        NOT NULL,
    id               uniqueidentifier    NOT NULL,
    data             json                NOT NULL,
    version          bigint              NOT NULL,
    last_modified    datetimeoffset      NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    created_at       datetimeoffset      NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    dotnet_type      varchar(500)        NULL,
CONSTRAINT pkey_pc_doc_deadletterevent_tenant_id_id PRIMARY KEY (tenant_id, id)
);
END
