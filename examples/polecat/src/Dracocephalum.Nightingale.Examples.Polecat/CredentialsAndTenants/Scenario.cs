using Dracocephalum.Nightingale.Client;
using Dracocephalum.Nightingale.Examples.Polecat.Common;
using Dracocephalum.Nightingale.Server.Data;

namespace Dracocephalum.Nightingale.Examples.Polecat.CredentialsAndTenants;

/// <summary>
/// Credentials and tenants against a Nightingale server, step by step: as the built-in
/// administrator, create an ops credential and a user bound to the default tenant, see what
/// each may and may not do, have the user change its own password, create a second tenant and
/// a credential bound to it, see that credential kept to its tenant and its appends invisible to
/// the default tenant's user under the same stream name, disable the tenant and see its
/// credential refused, and delete what was made. The program prints the steps; the test project
/// asserts the report.
/// </summary>
public static class Scenario
{
    /// <summary>What the run observed, in the order the steps happened.</summary>
    /// <param name="Made">The names of the credentials the administrator created, in order.</param>
    /// <param name="ReaderRefusal">How the user's attempt to create a group ended.</param>
    /// <param name="OperatorCreated">Whether the ops credential created the group.</param>
    /// <param name="ReaderReadAfterPasswordChange">Whether the user read the stream under its new password.</param>
    /// <param name="Tenant">The second tenant.</param>
    /// <param name="BoundRefusal">How the bound credential's read of the default tenant ended.</param>
    /// <param name="TenantRevision">The revision the bound credential's append reached in its own tenant.</param>
    /// <param name="TenantEvents">How many events the bound credential read back from its tenant's stream.</param>
    /// <param name="DefaultTenantEvents">How many events the default tenant's user saw under the same stream name.</param>
    /// <param name="DisabledRefusal">How the bound credential's call ended once its tenant was disabled.</param>
    /// <param name="Remaining">The credentials left when the run is done.</param>
    public sealed record Report(
        IReadOnlyList<string> Made,
        string ReaderRefusal,
        bool OperatorCreated,
        bool ReaderReadAfterPasswordChange,
        TenantInfo Tenant,
        string BoundRefusal,
        long TenantRevision,
        int TenantEvents,
        int DefaultTenantEvents,
        string DisabledRefusal,
        IReadOnlyList<string> Remaining);

    /// <summary>Runs the scenario.</summary>
    /// <param name="masterConnectionString">A connection string to the server's master database.</param>
    /// <param name="output">Where the steps are narrated.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report.</returns>
    public static async Task<Report> RunAsync(string masterConnectionString, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);

        await using var database = ExampleDatabase.Reserve(masterConnectionString);
        await output.WriteLineAsync($"1. Reserved the database name {database.Name}; the server creates it.").ConfigureAwait(false);

        await using var server = await ExampleServer.StartAsync(database.ConnectionString, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"2. Server listening at {server.Address}; the built-in administrator's password is the server's configuration.").ConfigureAwait(false);

        await using var asAdmin = server.Connect();
        var admin = asAdmin.Client.Management;
        var made = new List<string>();
        foreach (var (name, password, role) in new[] { ("operator", "operator-password-12", CredentialRole.Ops), ("reader", "reader-password-12", CredentialRole.User) })
        {
            made.Add((await admin.CreateCredentialAsync(name, password, role, Tenant.DefaultId, cancellationToken).ConfigureAwait(false)).Name);
        }

        await output.WriteLineAsync($"3. As the administrator, created {string.Join(" and ", made)}, an ops credential and a user, both bound to the default tenant.").ConfigureAwait(false);

        string readerRefusal;
        await using (var asReader = server.ConnectAs("reader", "reader-password-12"))
        {
            try
            {
                await asReader.Client.CreatePersistentSubscriptionAsync("orders-1", "billing", cancellationToken: cancellationToken).ConfigureAwait(false);
                readerRefusal = "nothing";
            }
            catch (AccessDeniedException refused)
            {
                readerRefusal = $"{nameof(AccessDeniedException)} ({refused.Required})";
            }
        }

        bool operatorCreated;
        await using (var asOperator = server.ConnectAs("operator", "operator-password-12"))
        {
            await asOperator.Client.CreatePersistentSubscriptionAsync("orders-1", "billing", cancellationToken: cancellationToken).ConfigureAwait(false);
            operatorCreated = (await asOperator.Client.ListPersistentSubscriptionsAsync("orders-1", cancellationToken).ConfigureAwait(false)).Count == 1;
        }

        await output.WriteLineAsync($"4. The user tried to create a group and was refused: {readerRefusal}; the ops credential created it.").ConfigureAwait(false);

        bool readerReadAfterChange;
        await using (var asReader = server.ConnectAs("reader", "reader-password-12"))
        {
            await asReader.Client.Management.ChangeOwnPasswordAsync("reader-password-12", "reader-password-13", cancellationToken).ConfigureAwait(false);
        }

        await using (var asReader = server.ConnectAs("reader", "reader-password-13"))
        {
            await using var result = asReader.Client.ReadStreamAsync(Direction.Forwards, "orders-1", StreamPosition.Start, cancellationToken: cancellationToken);
            await result.Head.ConfigureAwait(false);
            readerReadAfterChange = true;
        }

        await output.WriteLineAsync("5. The user changed its own password and read the stream under the new one.").ConfigureAwait(false);

        var tenant = await admin.CreateTenantAsync("acme", cancellationToken).ConfigureAwait(false);
        await admin.CreateCredentialAsync("acme-reader", "acme-password-12", CredentialRole.User, tenant.Id, cancellationToken).ConfigureAwait(false);
        string boundRefusal;
        await using (var asAcme = server.ConnectAs("acme-reader", "acme-password-12", Tenant.DefaultId))
        {
            try
            {
                await using var result = asAcme.Client.ReadStreamAsync(Direction.Forwards, "orders-1", StreamPosition.Start, cancellationToken: cancellationToken);
                await result.Head.ConfigureAwait(false);
                boundRefusal = "nothing";
            }
            catch (TenantNotFoundException)
            {
                boundRefusal = nameof(TenantNotFoundException);
            }
        }

        await output.WriteLineAsync($"6. Created the tenant {tenant.Name} ({tenant.Id}) and a credential bound to it; naming the default tenant with it was refused: {boundRefusal}.").ConfigureAwait(false);

        long tenantRevision;
        int tenantEvents;
        await using (var asAcme = server.ConnectAs("acme-reader", "acme-password-12"))
        {
            var placed = new EventData(Guid.NewGuid(), "order_placed", "{\"orderId\":\"acme-1\"}"u8.ToArray(), null);
            var paid = new EventData(Guid.NewGuid(), "order_paid", "{\"orderId\":\"acme-1\"}"u8.ToArray(), null);
            tenantRevision = (await asAcme.Client.AppendToStreamAsync("orders-1", StreamState.NoStream, [placed, paid], cancellationToken).ConfigureAwait(false)).Revision;
            tenantEvents = await CountAsync(asAcme.Client, cancellationToken).ConfigureAwait(false);
        }

        int defaultTenantEvents;
        await using (var asReader = server.ConnectAs("reader", "reader-password-13"))
        {
            defaultTenantEvents = await CountAsync(asReader.Client, cancellationToken).ConfigureAwait(false);
        }

        await output.WriteLineAsync($"7. The bound credential appended to orders-1 in its tenant, reaching revision {tenantRevision}, and read {tenantEvents} events back; the default tenant's user sees {defaultTenantEvents} under the same name.").ConfigureAwait(false);

        await admin.UpdateTenantAsync(tenant.Id, disabled: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        string disabledRefusal;
        await using (var asAcme = server.ConnectAs("acme-reader", "acme-password-12"))
        {
            try
            {
                await using var result = asAcme.Client.ReadStreamAsync(Direction.Forwards, "orders-1", StreamPosition.Start, cancellationToken: cancellationToken);
                await result.Head.ConfigureAwait(false);
                disabledRefusal = "nothing";
            }
            catch (TenantDisabledException)
            {
                disabledRefusal = nameof(TenantDisabledException);
            }
        }

        await output.WriteLineAsync($"8. Disabled the tenant: its credential's call ended with {disabledRefusal}.").ConfigureAwait(false);

        foreach (var name in new[] { "operator", "reader", "acme-reader" })
        {
            await admin.DeleteCredentialAsync(name, cancellationToken).ConfigureAwait(false);
        }

        var remaining = (await admin.ListCredentialsAsync(cancellationToken).ConfigureAwait(false)).Select(credential => credential.Name).ToList();
        await output.WriteLineAsync($"9. Deleted the credentials; {remaining.Count} remain. Dropping the database.").ConfigureAwait(false);

        return new Report(made, readerRefusal, operatorCreated, readerReadAfterChange, tenant, boundRefusal, tenantRevision, tenantEvents, defaultTenantEvents, disabledRefusal, remaining);
    }

    /// <summary>How many events <c>orders-1</c> holds in the caller's tenant: none when the name is not a stream there.</summary>
    private static async Task<int> CountAsync(NightingaleClient client, CancellationToken cancellationToken)
    {
        await using var read = client.ReadStreamAsync(Direction.Forwards, "orders-1", StreamPosition.Start, cancellationToken: cancellationToken);
        var count = 0;
        try
        {
            await foreach (var record in read.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                count += record is null ? 0 : 1;
            }
        }
        catch (StreamNotFoundException)
        {
            // The name is a stream in another tenant and none in this one.
        }

        return count;
    }
}
