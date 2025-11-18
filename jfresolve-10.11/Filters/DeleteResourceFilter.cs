using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Filters;

/// <summary>
/// Filter to intercept delete requests for Jfresolve virtual items
/// Allows deletion of virtual items that don't have physical files
/// </summary>
public sealed class DeleteResourceFilter : IAsyncActionFilter
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DeleteResourceFilter> _log;
    private readonly JfresolveManager _manager;
    private readonly IUserManager _userManager;

    public DeleteResourceFilter(
        ILibraryManager libraryManager,
        JfresolveManager manager,
        IUserManager userManager,
        ILogger<DeleteResourceFilter> log
    )
    {
        _libraryManager = libraryManager;
        _log = log;
        _manager = manager;
        _userManager = userManager;
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        // Only intercept DeleteItem actions with valid user
        if (
            ctx.GetActionName() != "DeleteItem"
            || !ctx.TryGetRouteGuid(out var guid)
            || !ctx.TryGetUserId(out var userId)
            || _userManager.GetUserById(userId) is not User user
        )
        {
            await next();
            return;
        }

        var item = _libraryManager.GetItemById<BaseItem>(guid, user);

        // Only handle Jfresolve items that user can delete
        if (item is null || !_manager.IsJfresolve(item) || !_manager.CanDelete(item, user))
        {
            await next();
            return;
        }

        // Handle deletion and return 204 No Content
        _log.LogInformation("Jfresolve: Deleting item '{Name}' (ID: {Id})", item.Name, item.Id);
        _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, true);
        ctx.Result = new NoContentResult();
    }
}
