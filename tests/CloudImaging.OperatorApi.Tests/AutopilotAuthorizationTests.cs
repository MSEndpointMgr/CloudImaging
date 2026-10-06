using System.Reflection;
using CloudImaging.OperatorApi.Functions;
using CloudImaging.OperatorApi.Middleware;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Xunit;

namespace CloudImaging.OperatorApi.Tests;

/// <summary>
/// Autopilot approvals are a portal-only capability: the Media Builder service role must never
/// reach them, because the deciding user's identity is only ever supplied by the portal backend.
/// </summary>
public sealed class AutopilotAuthorizationTests
{
    [Fact]
    public void MediaBuilderAccess_CannotReachAnyAutopilotFunction()
    {
        var allowlist = (HashSet<string>)typeof(AppRoleAuthorizationMiddleware)
            .GetField("MediaBuilderAllowedFunctions", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        var autopilotFunctions = typeof(AutopilotFunctions)
            .GetMethods()
            .Select(m => m.GetCustomAttribute<FunctionAttribute>()?.Name)
            .OfType<string>()
            .ToList();

        autopilotFunctions.Should().NotBeEmpty();
        autopilotFunctions.Should().NotIntersectWith(allowlist);
    }
}
