using Dracocephalum.Nightingale.Protocol.V1;
using Dracocephalum.Nightingale.Server.Auth;
using Dracocephalum.Nightingale.Server.Data;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Server;

/// <summary>Manages tenants; a global admin's call, every one of them.</summary>
/// <param name="tenants">The tenants table.</param>
public sealed class TenantsService(ITenantStore tenants) : Protocol.V1.Tenants.TenantsBase
{
    /// <inheritdoc/>
    public override async Task<TenantResponse> Create(CreateTenantRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        await RequireGlobalAdminAsync(context).ConfigureAwait(false);
        var name = RequireName(request.Name);
        try
        {
            return new TenantResponse { Tenant = ToInfo(await tenants.CreateAsync(name, context.CancellationToken).ConfigureAwait(false)) };
        }
        catch (TenantExistsException)
        {
            throw NightingaleErrors.TenantExists(name);
        }
    }

    /// <inheritdoc/>
    public override async Task<TenantResponse> Update(UpdateTenantRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        await RequireGlobalAdminAsync(context).ConfigureAwait(false);
        if (!Guid.TryParseExact(request.Id, "D", out var id))
        {
            throw NightingaleErrors.InvalidArgument("A tenant id is a UUID in its canonical form.");
        }

        var name = request.Name.Length == 0 ? null : RequireName(request.Name);
        try
        {
            var tenant = await tenants.UpdateAsync(id, name, request.HasDisabled ? request.Disabled : null, context.CancellationToken).ConfigureAwait(false)
                ?? throw NightingaleErrors.TenantNotFound(request.Id);
            return new TenantResponse { Tenant = ToInfo(tenant) };
        }
        catch (TenantExistsException)
        {
            throw NightingaleErrors.TenantExists(name ?? string.Empty);
        }
    }

    /// <inheritdoc/>
    public override async Task<ListTenantsResponse> List(ListTenantsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        await RequireGlobalAdminAsync(context).ConfigureAwait(false);
        var response = new ListTenantsResponse();
        response.Tenants.AddRange((await tenants.ListAsync(context.CancellationToken).ConfigureAwait(false)).Select(ToInfo));
        return response;
    }

    private static Protocol.V1.TenantInfo ToInfo(Tenant row) => new()
    {
        Id = row.Id.ToString("D"),
        Name = row.Name,
        Disabled = row.IsDisabled,
        CreatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(row.CreatedAt),
    };

    private static async Task RequireGlobalAdminAsync(ServerCallContext context)
    {
        // Tenants are above any tenant, so the call carries no tenant header and is authorized
        // on the role alone: a tenant-bound admin is refused.
        var principal = await context.RequireRoleAsync(CredentialRole.Admin).ConfigureAwait(false);
        if (principal.TenantId is not null)
        {
            throw NightingaleErrors.AccessDenied("Tenants are managed by a global administrator.");
        }
    }

    private static string RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 250)
        {
            throw NightingaleErrors.InvalidArgument("A tenant's name is 1 to 250 characters.");
        }

        return name;
    }
}
