using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Dracocephalum.Nightingale.Server.Auth;
using Dracocephalum.Nightingale.Server.Data;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Server.Grpc;

/// <summary>
/// The gRPC face of <see cref="TenantManager"/>: each call reads its request off the wire,
/// hands the caller and the parsed request to the manager, and writes the answer, or the
/// refusal as the status the contract promises, back.
/// </summary>
/// <param name="manager">The tenants, for any transport.</param>
public sealed class TenantsService(TenantManager manager) : Protocols.Grpc.V1.Tenants.TenantsBase
{
    /// <inheritdoc/>
    public override Task<TenantResponse> Create(CreateTenantRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return ServeAsync(request.Name, async () => new TenantResponse
        {
            Tenant = ToInfo(await manager.CreateAsync(context.GetPrincipal(), request.Name, context.CancellationToken).ConfigureAwait(false)),
        });
    }

    /// <inheritdoc/>
    public override Task<TenantResponse> Update(UpdateTenantRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!Guid.TryParseExact(request.Id, "D", out var id))
        {
            throw NightingaleErrors.InvalidArgument("A tenant id is a UUID in its canonical form.");
        }

        var name = request.Name.Length == 0 ? null : request.Name;
        return ServeAsync(name ?? string.Empty, async () => new TenantResponse
        {
            Tenant = ToInfo(await manager.UpdateAsync(context.GetPrincipal(), id, name, request.HasDisabled ? request.Disabled : null, context.CancellationToken).ConfigureAwait(false)),
        });
    }

    /// <inheritdoc/>
    public override Task<ListTenantsResponse> List(ListTenantsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return ServeAsync(string.Empty, async () =>
        {
            var response = new ListTenantsResponse();
            response.Tenants.AddRange((await manager.ListAsync(context.GetPrincipal(), context.CancellationToken).ConfigureAwait(false)).Select(ToInfo));
            return response;
        });
    }

    /// <summary>Runs a call and maps what the manager refused to the wire; a taken name is reported with the name the request carried.</summary>
    private static async Task<TResponse> ServeAsync<TResponse>(string name, Func<Task<TResponse>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (TenantExistsException)
        {
            throw NightingaleErrors.TenantExists(name);
        }
        catch (Exception exception) when (NightingaleErrors.TryTranslate(exception, out var status))
        {
            throw status;
        }
    }

    private static Protocols.Grpc.V1.TenantInfo ToInfo(Tenant row) => new()
    {
        Id = row.Id.ToString("D"),
        Name = row.Name,
        Disabled = row.IsDisabled,
        CreatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(row.CreatedAt),
    };
}
