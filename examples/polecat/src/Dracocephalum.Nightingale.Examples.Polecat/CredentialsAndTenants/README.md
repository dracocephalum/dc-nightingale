# credentials-and-tenants

Credentials and tenants against a Nightingale server, as a program that
narrates each step and as a test that asserts the same run.

## What it does

Creates a tenant of its own in the run's database and drives the run's
server, hosted in the program's own process on a loopback port, through the
client as the built-in administrator, whose password is the server's
configuration: create an ops credential and a user bound to the scenario's
tenant, see the user refused a group's creation and the ops credential
allowed it, have the user change its own password and read under the new
one, create a second tenant and a credential bound to it, see that credential
refused when it names the first tenant, have it append to `orders-1` in its
own tenant and read the events back while the first tenant's user sees none
under the same name, disable the tenant and see its credential refused, and
delete what was made.
The scenario's test runs exactly the same code and asserts the report it
returns.

## Run it

From the component's folder:

    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- credentials-and-tenants

Its test is `CredentialsAndTenantsTests` in the component's test project.

## Notes

Every sample connects as the built-in administrator, which is global, so its
connection string names the scenario's tenant, and the run's management calls
name none; the credentials made here are bound to a tenant and send none. The
second tenant's name carries a number the run gives out once, as every
tenant's does, because a tenant's name is unique and a scenario may run twice
in one run. A disabled tenant is refused at once on the instance that
disabled it and within the refresh interval on the others.
