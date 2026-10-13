using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Nabo.Recipes;

[ApiController, Route("api/admin/recipes"), Authorize]
public class RecipeController(RecipeStore store) : ControllerBase
{
    async Task<bool> IsEditor(CancellationToken ct) => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && await store.IsEditor(id, ct);
    [HttpGet("access")]
    public async Task<IActionResult> Access(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return await IsEditor(ct) ? Ok(new { authorized = true }) : Forbid();
    }
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return await IsEditor(ct) ? Ok(await store.List(ct)) : Forbid();
    }
    public record SaveRequest(JsonElement Document, int Revision, bool Publish);
    [HttpPut("{slug}"), RequestSizeLimit(262144)]
    public async Task<IActionResult> Save(string slug, SaveRequest input, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsEditor(ct)) return Forbid();
        if (input.Revision < 0 || input.Revision == int.MaxValue || !RecipeValidation.Valid(slug, input.Document))
            return BadRequest(new { message = "Проверьте название, ингредиенты, запросы поиска и данные рецепта." });
        var revision = await store.Save(slug, input.Document, input.Revision, input.Publish, ct);
        return revision is null ? Conflict(new { message = "Рецепт изменён в другой вкладке. Обновите список перед сохранением." }) : Ok(new { revision });
    }

    public record CategoryRequest(string CategoryId, int Revision);
    [HttpPatch("{slug}/category")]
    public async Task<IActionResult> Category(string slug, CategoryRequest input, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsEditor(ct)) return Forbid();
        if (input.Revision < 1 || input.Revision == int.MaxValue || !RecipeValidation.ValidCategory(input.CategoryId) ||
            !System.Text.RegularExpressions.Regex.IsMatch(slug,"^[a-z0-9]+(?:-[a-z0-9]+)*$") || slug.Length > 100)
            return BadRequest(new { message = "Укажите существующую категорию и текущую версию рецепта." });
        var revision = await store.SetCategory(slug,input.CategoryId,input.Revision,ct);
        return revision is null ? Conflict(new { message = "Рецепт изменён. Обновите список." }) : Ok(new { revision });
    }
    [HttpDelete("{slug}")]
    public async Task<IActionResult> Delete(string slug, [FromQuery] int revision, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsEditor(ct)) return Forbid();
        if (revision < 1 || !System.Text.RegularExpressions.Regex.IsMatch(slug, "^[a-z0-9]+(?:-[a-z0-9]+)*$") || slug.Length > 100)
            return BadRequest(new { message = "Укажите адрес рецепта и его текущую версию." });
        return await store.Delete(slug, revision, ct)
            ? NoContent()
            : Conflict(new { message = "Рецепт уже удалён или изменён в другой вкладке. Обновите список." });
    }
}
[ApiController, Route("api/recipe-catalog")]
public class CatalogController(RecipeStore store) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await store.Published(ct));
    }
}
