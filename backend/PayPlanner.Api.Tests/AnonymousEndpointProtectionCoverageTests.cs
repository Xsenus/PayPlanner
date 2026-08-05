using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using PayPlanner.Api.Controllers;
using Xunit;

namespace PayPlanner.Api.Tests;

/// <summary>
/// Архитектурный барьер для новых публичных маршрутов: анонимный endpoint нельзя
/// добавить без явного ограничения частоты, а POST — без ограничения тела.
/// </summary>
public sealed class AnonymousEndpointProtectionCoverageTests
{
    [Fact]
    public void EveryAnonymousEndpointHasRateLimitAndEveryAnonymousPostHasBodyLimit()
    {
        var controllerAssembly = typeof(AuthController).Assembly;
        var anonymousEndpoints = controllerAssembly.ExportedTypes
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any())
                .Where(method => HasAttribute<AllowAnonymousAttribute>(type, method))
                .Select(method => new { Controller = type, Method = method }))
            .ToList();

        Assert.NotEmpty(anonymousEndpoints);
        foreach (var endpoint in anonymousEndpoints)
        {
            Assert.True(
                HasAttribute<EnableRateLimitingAttribute>(endpoint.Controller, endpoint.Method),
                $"Анонимный endpoint {endpoint.Controller.Name}.{endpoint.Method.Name} не имеет rate limit.");

            var acceptsPost = endpoint.Method.GetCustomAttributes<HttpMethodAttribute>()
                .Any(attribute => attribute.HttpMethods.Contains(HttpMethods.Post, StringComparer.OrdinalIgnoreCase));
            if (acceptsPost)
            {
                Assert.True(
                    HasAttribute<RequestSizeLimitAttribute>(endpoint.Controller, endpoint.Method),
                    $"Анонимный POST {endpoint.Controller.Name}.{endpoint.Method.Name} не ограничивает тело запроса.");
            }
        }
    }

    private static bool HasAttribute<TAttribute>(MemberInfo controller, MemberInfo method)
        where TAttribute : Attribute
        => method.GetCustomAttributes<TAttribute>().Any()
            || controller.GetCustomAttributes<TAttribute>().Any();
}
