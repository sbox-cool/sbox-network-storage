using System.Runtime.CompilerServices;

// The unit-test assembly calls the rate-limit helpers directly; no other assembly gets internal access.
[assembly: InternalsVisibleTo( "network-storage.unittest" )]
