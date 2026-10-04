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
    seq_id              bigint              NOT NULL IDENTITY,
    id                  uniqueidentifier    NOT NULL,
    stream_id           varchar(250)        NOT NULL,
    version             bigint              NOT NULL,
    data                json                NOT NULL,
    bdata               varbinary(max)      NULL,
    type                varchar(500)        NOT NULL,
    timestamp           datetimeoffset      NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    tenant_id           varchar(250)        NOT NULL DEFAULT '*DEFAULT*',
    dotnet_type         varchar(500)        NULL,
    correlation_id      varchar(250)        NULL,
    causation_id        varchar(250)        NULL,
    headers             json                NULL,
    is_archived         bit                 NOT NULL DEFAULT 0,
    category AS (LEFT(stream_id, CHARINDEX('-', stream_id + '-') - 1)) PERSISTED,
    category_ordinal    bigint              NULL,
    type_ordinal        bigint              NULL,
CONSTRAINT pkey_pc_events_seq_id PRIMARY KEY (seq_id)
);
END

CREATE UNIQUE INDEX ix_pc_events_stream_and_version ON dbo.pc_events (tenant_id, stream_id, version);

CREATE INDEX ix_pc_events_category_seq ON dbo.pc_events (tenant_id, category, seq_id) WHERE (is_archived = 0);

CREATE INDEX ix_pc_events_type_seq ON dbo.pc_events (tenant_id, type, seq_id) WHERE (is_archived = 0);

CREATE INDEX ix_pc_events_category_ordinal ON dbo.pc_events (tenant_id, category, category_ordinal) WHERE (category_ordinal IS NOT NULL);

CREATE INDEX ix_pc_events_type_ordinal ON dbo.pc_events (tenant_id, type, type_ordinal) WHERE (type_ordinal IS NOT NULL);
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
