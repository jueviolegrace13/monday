using Monday.Domain.Tests;

// A minimal runner so the domain rules are tested from day one, before NuGet (xUnit) is reachable.
// Each test is a public static method on a *Tests class taking no arguments; a thrown exception fails it.
return TestRunner.Run(typeof(TestRunner).Assembly);
