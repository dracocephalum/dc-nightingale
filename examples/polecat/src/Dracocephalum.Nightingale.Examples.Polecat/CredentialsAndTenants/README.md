# credentials-and-tenants

Credentials and tenants against a Nightingale server, as a program that
narrates each step and as a test that asserts the same run.

## What it does

Reserves a random database name on the local SQL Server, hosts the
Polecat-backed server in its own process on a loopback port, which creates and
initializes that database, and drives it through the client as the built-in
administrator, whose password is the server's configuration: create an ops
credential and a user bound to the default tenant, see the user refused a
group's creation and the ops credential allowed it, have the user change its
own password and read under the new one, create a second tenant and a
credential bound to it, see that credential refused when it names the
default tenant, have it append to `orders-1` in its own tenant and read the
events back while the default tenant's user sees none under the same name,
disable the tenant and see its credential refused, and delete what was made.
Then it drops the database.
The scenario's test runs exactly the same code and asserts the report it
returns.

## Run it

From the component's folder:

    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- credentials-and-tenants

Its test is `CredentialsAndTenantsTests` in the component's test project.

## Notes

Every sample connects as the built-in administrator, which is global, so its
connection string names the default tenant; the credentials made here are
bound to a tenant and send none. A disabled tenant is refused at once on the
instance that disabled it and within the refresh interval on the others.
