# cluster-redirect

Two server instances over one store, as a program that narrates each step and
as a test that asserts the same run.

## What it does

Reserves a random database name on the local SQL Server, hosts two
Polecat-backed server instances in its own process, each on a loopback port of
its own and both over that one database, and drives them through two clients:
append two events and create a group with room for one consumer through the
second instance, which any instance serves; connect a consumer to the first instance, which then runs the
group and writes its address on the group's lease; ask the second instance for
a second consumer and see the client sent to the first and refused there,
where the group's limit is kept; replay the parked message through the second
instance and see the client sent to the first, where the consumer receives it
at once; and open a third client from one connection string naming both
instances, which reads the stream from each in rotation. Then it disposes the consumer and drops the database.
The scenario's test runs exactly the same code and asserts the report it
returns.

## Run it

From the component's folder:

    dotnet run --project src/Dracocephalum.Nightingale.Examples.Polecat -- cluster-redirect

Its test is `ClusterRedirectTests` in the component's test project.

## Notes

Neither instance is configured with an address: each derives the one it
advertises from what it listens on, which here is a loopback port. An instance
behind address translation sets `Nightingale:Cluster:AdvertisedAddress`
instead. The client follows the address once, the way the reference client
follows a not-leader answer, and keeps the connection for the next time.
