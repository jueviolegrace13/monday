using System.Reflection;
using Monday.Domain.Common;

namespace Monday.Domain.Tests;

internal static class TestRunner
{
    public static int Run(Assembly assembly)
    {
        var tests = assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Tests", StringComparison.Ordinal))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.GetParameters().Length == 0))
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.Name)
            .ToList();

        var failed = 0;
        foreach (var test in tests)
        {
            var name = $"{test.DeclaringType!.Name}.{test.Name}";
            try
            {
                test.Invoke(null, null);
                Console.WriteLine($"  pass  {name}");
            }
            catch (TargetInvocationException ex)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name}: {ex.InnerException?.Message}");
            }
        }

        Console.WriteLine($"{tests.Count - failed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    /// <summary>Asserts the action breaks a domain rule with the given code.</summary>
    public static TException Throws<TException>(Action action, string? code = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            if (code is not null && ex is DomainException domain && domain.Code != code)
            {
                throw new InvalidOperationException($"Expected rule '{code}', got '{domain.Code}'.");
            }

            return ex;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}{(code is null ? string.Empty : $" ({code})")}, nothing was thrown.");
    }
}
