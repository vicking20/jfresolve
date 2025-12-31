using System;
using System.Linq;
using Microsoft.AspNetCore.Http;
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
    /// Get the action name from the ActionExecutingContext
    /// </summary>
    public static string? GetActionName(this ActionExecutingContext ctx) =>
        (ctx.ActionDescriptor as ControllerActionDescriptor)?.ActionName;

    /// <summary>
    /// Get the action name from the HttpContext (Gelato pattern)
    /// </summary>
    public static string? GetActionName(this HttpContext ctx) =>
        ctx.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ActionName;

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

/// <summary>
/// Extension methods for storing metadata in ExternalId (Gelato pattern)
/// </summary>
public static class BaseItemExtensions
{
    public static string JfresolveData(this MediaBrowser.Controller.Entities.BaseItem item, string key)
    {
        if (string.IsNullOrEmpty(item.ExternalId))
            return string.Empty;

        try
        {
            var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(item.ExternalId);
            return data?.GetValueOrDefault(key) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static void SetJfresolveData(this MediaBrowser.Controller.Entities.BaseItem item, string key, string value)
    {
        Dictionary<string, string> data;

        try
        {
            data = string.IsNullOrEmpty(item.ExternalId)
                ? new Dictionary<string, string>()
                : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(item.ExternalId)
                    ?? new Dictionary<string, string>();
        }
        catch
        {
            data = new Dictionary<string, string>();
        }

        data[key] = value;
        item.ExternalId = System.Text.Json.JsonSerializer.Serialize(data);
    }
}
