using System.Text;

using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
using Dracocephalum.Nightingale.Server.Auth;
using Dracocephalum.Nightingale.Server.Data;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// Manages credentials. Every call but the last needs the admin role; a tenant-bound admin sees
/// and changes its own tenant's credentials only, and nobody gives a credential a role above
/// their own or a tenant other than their own. A change drops what this instance remembered of
/// the credential; the other instances drop it when their memory lapses.
/// </summary>
/// <param name="credentials">The credentials table.</param>
/// <param name="hasher">Makes and checks hashes.</param>
/// <param name="options">The host's options, for the password rule.</param>
/// <param name="time">The clock.</param>
/// <param name="basic">Where this instance remembers verified credentials; none in a host without authentication.</param>
public sealed class CredentialsService(ICredentialStore credentials, PasswordHasher hasher, NightingaleOptionsBase options, TimeProvider time, BasicAuthenticator? basic = null) : Protocol.V1.Credentials.CredentialsBase
{
    /// <inheritdoc/>
    public override async Task<CredentialResponse> Create(CreateCredentialRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var caller = await RequireAdminAsync(context).ConfigureAwait(false);
        var name = RequireName(request.Name);
        RequirePassword(request.Password);
        var role = request.Role.ToCredentialRole() ?? CredentialRole.User;
        var tenant = ParseTenant(request.Tenant, allowGlobal: true);
        RequireWithin(caller, role, tenant);

        var now = time.GetUtcNow();
        var row = new Credential
        {
            Id = Guid.NewGuid(),
            Name = name,
            PasswordHash = hasher.Hash(request.Password),
            Role = role,
            TenantId = tenant,
            SecurityStamp = Guid.NewGuid(),
            PasswordChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        try
        {
            await credentials.CreateAsync(row, context.CancellationToken).ConfigureAwait(false);
        }
        catch (CredentialExistsException)
        {
            throw NightingaleErrors.CredentialExists(name);
        }

        return new CredentialResponse { Credential = ToInfo(row) };
    }

    /// <inheritdoc/>
    public override async Task<CredentialResponse> Update(UpdateCredentialRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var caller = await RequireAdminAsync(context).ConfigureAwait(false);
        var row = await FindManagedAsync(caller, request.Name, context.CancellationToken).ConfigureAwait(false);
        var role = request.Role.ToCredentialRole();
        Guid? tenant = request.Tenant.Length == 0 ? null : request.Tenant == "-" ? Guid.Empty : ParseTenant(request.Tenant, allowGlobal: false);
        RequireWithin(caller, role ?? row.Role, tenant is null ? row.TenantId : tenant == Guid.Empty ? null : tenant);

        var updated = await credentials.UpdateAsync(row.Id, role, tenant, request.HasDisabled ? request.Disabled : null, null, context.CancellationToken).ConfigureAwait(false)
            ?? throw NightingaleErrors.CredentialNotFound(request.Name);
        basic?.Forget(updated.Name);
        return new CredentialResponse { Credential = ToInfo(updated) };
    }

    /// <inheritdoc/>
    public override async Task<CredentialResponse> SetPassword(SetPasswordRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var caller = await RequireAdminAsync(context).ConfigureAwait(false);
        var row = await FindManagedAsync(caller, request.Name, context.CancellationToken).ConfigureAwait(false);
        RequirePassword(request.Password);
        var updated = await credentials.UpdateAsync(row.Id, null, null, null, hasher.Hash(request.Password), context.CancellationToken).ConfigureAwait(false)
            ?? throw NightingaleErrors.CredentialNotFound(request.Name);
        basic?.Forget(updated.Name);
        return new CredentialResponse { Credential = ToInfo(updated) };
    }

    /// <inheritdoc/>
    public override async Task<DeleteCredentialResponse> Delete(DeleteCredentialRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var caller = await RequireAdminAsync(context).ConfigureAwait(false);
        var row = await FindManagedAsync(caller, request.Name, context.CancellationToken).ConfigureAwait(false);
        if (!await credentials.DeleteAsync(row.Id, context.CancellationToken).ConfigureAwait(false))
        {
            throw NightingaleErrors.CredentialNotFound(request.Name);
        }

        basic?.Forget(row.Name);
        return new DeleteCredentialResponse();
    }

    /// <inheritdoc/>
    public override async Task<ListCredentialsResponse> List(ListCredentialsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var caller = await RequireAdminAsync(context).ConfigureAwait(false);
        var rows = await credentials.ListAsync(caller.TenantId, context.CancellationToken).ConfigureAwait(false);
        var response = new ListCredentialsResponse();
        response.Credentials.AddRange(rows.Select(ToInfo));
        return response;
    }

    /// <inheritdoc/>
    public override async Task<ChangeOwnPasswordResponse> ChangeOwnPassword(ChangeOwnPasswordRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var caller = context.GetPrincipal();
        if (string.Equals(caller.Name, UserCredentials.AdminUserName, StringComparison.OrdinalIgnoreCase) && caller.Scheme != BasicAuthenticator.Scheme)
        {
            throw NightingaleErrors.AccessDenied("The built-in administrator's password is the server's configuration.");
        }

        var row = await credentials.FindAsync(caller.Name, context.CancellationToken).ConfigureAwait(false)
            ?? throw NightingaleErrors.AccessDenied("The built-in administrator's password is the server's configuration.");
        if (!hasher.Verify(request.CurrentPassword, row.PasswordHash).Matches)
        {
            throw NightingaleErrors.AuthenticationFailed("The current password is not right.");
        }

        RequirePassword(request.NewPassword);
        await credentials.UpdateAsync(row.Id, null, null, null, hasher.Hash(request.NewPassword), context.CancellationToken).ConfigureAwait(false);
        basic?.Forget(row.Name);
        return new ChangeOwnPasswordResponse();
    }

    private static Protocol.V1.CredentialInfo ToInfo(Credential row)
    {
        var info = new Protocol.V1.CredentialInfo
        {
            Name = row.Name,
            Role = row.Role.ToWire(),
            Tenant = row.TenantId?.ToString("D") ?? string.Empty,
            Disabled = row.IsDisabled,
            CreatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(row.CreatedAt),
            PasswordChangedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(row.PasswordChangedAt),
        };
        if (row.LockedUntil is { } until)
        {
            info.LockedUntil = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(until);
        }

        return info;
    }

    private static Task<NightingalePrincipal> RequireAdminAsync(ServerCallContext context) =>
        context.RequireRoleAsync(CredentialRole.Admin);

    private static string RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 250 || name.Contains(':', StringComparison.Ordinal) || string.Equals(name, UserCredentials.AdminUserName, StringComparison.OrdinalIgnoreCase))
        {
            throw NightingaleErrors.InvalidArgument("A credential's name is 1 to 250 characters, holds no ':', and is not the built-in administrator's.");
        }

        return name;
    }

    private void RequirePassword(string password)
    {
        if (password is null || Encoding.UTF8.GetByteCount(password) > 1024 || password.Normalize(NormalizationForm.FormKC).Length < options.Auth.MinimumPasswordLength)
        {
            throw NightingaleErrors.InvalidArgument($"A password is at least {options.Auth.MinimumPasswordLength} characters and at most 1024 bytes.");
        }
    }

    private static Guid? ParseTenant(string tenant, bool allowGlobal)
    {
        if (tenant.Length == 0)
        {
            return allowGlobal ? null : throw NightingaleErrors.InvalidArgument("A tenant id is a UUID in its canonical form.");
        }

        return Guid.TryParseExact(tenant, "D", out var id) ? id : throw NightingaleErrors.InvalidArgument("A tenant id is a UUID in its canonical form.");
    }

    /// <summary>Nobody grants above their own role, and a tenant-bound admin grants within its tenant only.</summary>
    private static void RequireWithin(NightingalePrincipal caller, CredentialRole role, Guid? tenant)
    {
        if (role > caller.Role)
        {
            throw NightingaleErrors.AccessDenied("A credential cannot be given a role above its maker's.");
        }

        if (caller.TenantId is { } own && tenant != own)
        {
            throw NightingaleErrors.AccessDenied("A tenant-bound administrator manages credentials of its own tenant only.");
        }
    }

    /// <summary>The credential, among those the caller may manage; another tenant's is not found, so that it is not revealed.</summary>
    private async Task<Credential> FindManagedAsync(NightingalePrincipal caller, string name, CancellationToken cancellationToken)
    {
        var row = await credentials.FindAsync(name, cancellationToken).ConfigureAwait(false);
        if (row is null || (caller.TenantId is { } own && row.TenantId != own))
        {
            throw NightingaleErrors.CredentialNotFound(name);
        }

        return row;
    }
}
