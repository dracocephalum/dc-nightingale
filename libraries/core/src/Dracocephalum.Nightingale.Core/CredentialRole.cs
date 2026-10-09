namespace Dracocephalum.Nightingale;

/// <summary>
/// What a credential may do. Each role includes the ones below it. A credential is bound to one
/// tenant or is global; a tenant-bound credential acts within its tenant only, a global one names
/// the tenant on every data call, and a global <see cref="Admin"/> manages tenants and global
/// credentials as well.
/// </summary>
public enum CredentialRole
{
    /// <summary>Reads, appends and subscribes.</summary>
    User = 0,

    /// <summary>Plus management of persistent-subscription groups and of streams: create, update, delete, replay, skip, tombstone.</summary>
    Ops = 1,

    /// <summary>Plus management of credentials; globally, of tenants too.</summary>
    Admin = 2,
}
