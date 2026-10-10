using Dracocephalum.Nightingale.Protocols.Grpc;
using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Dracocephalum.Nightingale.Server.Auth;
using Dracocephalum.Nightingale.Server.Data;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Server.Grpc;

/// <summary>
/// The gRPC face of <see cref="CredentialManager"/>: each call reads its request off the wire,
/// hands the caller and the parsed request to the manager, and writes the answer, or the
/// refusal as the status the contract promises, back.
/// </summary>
/// <param name="manager">The credentials, for any transport.</param>
public sealed class CredentialsService(CredentialManager manager) : Credentials.CredentialsBase
{
    /// <inheritdoc/>
    public override Task<CredentialResponse> Create(CreateCredentialRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var tenant = ParseTenant(request.Tenant, allowGlobal: true);
        var credential = new NewCredential(request.Name, request.Password, request.Role.ToCredentialRole() ?? CredentialRole.User, tenant);
        return ServeAsync(request.Name, async () => new CredentialResponse
        {
            Credential = ToInfo(await manager.CreateAsync(context.GetPrincipal(), credential, context.CancellationToken).ConfigureAwait(false)),
        });
    }

    /// <inheritdoc/>
    public override Task<CredentialResponse> Update(UpdateCredentialRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        TenantBinding? binding = request.Tenant.Length == 0
            ? null
            : request.Tenant == "-" ? TenantBinding.Global : new TenantBinding(ParseTenant(request.Tenant, allowGlobal: false));
        var changes = new CredentialChanges(request.Role.ToCredentialRole(), binding, request.HasDisabled ? request.Disabled : null);
        return ServeAsync(request.Name, async () => new CredentialResponse
        {
            Credential = ToInfo(await manager.UpdateAsync(context.GetPrincipal(), request.Name, changes, context.CancellationToken).ConfigureAwait(false)),
        });
    }

    /// <inheritdoc/>
    public override Task<CredentialResponse> SetPassword(SetPasswordRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return ServeAsync(request.Name, async () => new CredentialResponse
        {
            Credential = ToInfo(await manager.SetPasswordAsync(context.GetPrincipal(), request.Name, request.Password, context.CancellationToken).ConfigureAwait(false)),
        });
    }

    /// <inheritdoc/>
    public override Task<DeleteCredentialResponse> Delete(DeleteCredentialRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return ServeAsync(request.Name, async () =>
        {
            await manager.DeleteAsync(context.GetPrincipal(), request.Name, context.CancellationToken).ConfigureAwait(false);
            return new DeleteCredentialResponse();
        });
    }

    /// <inheritdoc/>
    public override Task<ListCredentialsResponse> List(ListCredentialsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return ServeAsync(string.Empty, async () =>
        {
            var response = new ListCredentialsResponse();
            response.Credentials.AddRange((await manager.ListAsync(context.GetPrincipal(), context.CancellationToken).ConfigureAwait(false)).Select(ToInfo));
            return response;
        });
    }

    /// <inheritdoc/>
    public override Task<ChangeOwnPasswordResponse> ChangeOwnPassword(ChangeOwnPasswordRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return ServeAsync(context.GetPrincipal().Name, async () =>
        {
            await manager.ChangeOwnPasswordAsync(context.GetPrincipal(), request.CurrentPassword, request.NewPassword, context.CancellationToken).ConfigureAwait(false);
            return new ChangeOwnPasswordResponse();
        });
    }

    /// <summary>Runs a call and maps what the manager refused to the wire; the two refusals that name the credential get the name the request carried.</summary>
    private static async Task<TResponse> ServeAsync<TResponse>(string name, Func<Task<TResponse>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (CredentialExistsException)
        {
            throw NightingaleErrors.CredentialExists(name);
        }
        catch (CredentialNotFoundException)
        {
            throw NightingaleErrors.CredentialNotFound(name);
        }
        catch (Exception exception) when (NightingaleErrors.TryTranslate(exception, out var status))
        {
            throw status;
        }
    }

    private static Protocols.Grpc.V1.CredentialInfo ToInfo(Credential row)
    {
        var info = new Protocols.Grpc.V1.CredentialInfo
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

    private static Guid? ParseTenant(string tenant, bool allowGlobal)
    {
        if (tenant.Length == 0)
        {
            return allowGlobal ? null : throw NightingaleErrors.InvalidArgument("A tenant id is a UUID in its canonical form.");
        }

        return Guid.TryParseExact(tenant, "D", out var id) ? id : throw NightingaleErrors.InvalidArgument("A tenant id is a UUID in its canonical form.");
    }
}
