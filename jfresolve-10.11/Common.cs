using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jfresolve;

/// <summary>
/// Common extension methods and utilities for Jfresolve filters
/// </summary>
public static class Common
{
    private static readonly string[] RouteGuidKeys = new[] { "id", "Id", "ID", "itemId", "ItemId", "ItemID" };

    /// <summary>
    /// Get the action name from the context
    /// </summary>
    public static string? GetActionName(this ActionExecutingContext ctx) =>
        (ctx.ActionDescriptor as ControllerActionDescriptor)?.ActionName;

    /// <summary>
    /// Try to get a GUID from the route values
    /// </summary>
    public static bool TryGetRouteGuid(this ActionExecutingContext ctx, out Guid value)
    {
        value = Guid.Empty;
        return ctx.TryGetRouteGuidString(out var s) && Guid.TryParse(s, out value);
    }

    /// <summary>
    /// Try to get a GUID string from the route values
    /// </summary>
    public static bool TryGetRouteGuidString(this ActionExecutingContext ctx, out string value)
    {
        value = string.Empty;

        // Check if already resolved
        if (ctx.HttpContext.Items["GuidResolved"] is Guid g)
        {
            value = g.ToString("N");
            return true;
        }

        var rd = ctx.RouteData.Values;

        // Check route values
        foreach (var key in RouteGuidKeys)
        {
            if (
                rd.TryGetValue(key, out var raw)
                && raw?.ToString() is string s
                && !string.IsNullOrWhiteSpace(s)
            )
            {
                value = s;
                return true;
            }
        }

        // Fallback: check query string "ids"
        var query = ctx.HttpContext.Request.Query;
        if (
            query.TryGetValue("ids", out var ids)
            && ids.Count == 1
            && !string.IsNullOrWhiteSpace(ids[0])
        )
        {
            value = ids[0]!;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Try to get the user ID from the HTTP context claims
    /// </summary>
    public static bool TryGetUserId(this ActionExecutingContext ctx, out Guid userId)
    {
        userId = Guid.Empty;

        var userIdStr = ctx
            .HttpContext.User.Claims.FirstOrDefault(c => c.Type is "UserId" or "Jellyfin-UserId")
            ?.Value;

        return Guid.TryParse(userIdStr, out userId);
    }
}
