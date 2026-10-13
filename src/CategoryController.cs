using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Npgsql;
namespace Nabo.Recipes;
[ApiController,Route("api/recipe-categories")]
public class PublicCategoryController(CategoryStore categories):ControllerBase
{
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(new { categories=await categories.List(ct) });
}
[ApiController,Route("api/admin/recipe-categories"),Authorize]
public class CategoryController(CategoryStore categories,RecipeStore recipes):ControllerBase
{
    async Task<bool> Editor(CancellationToken ct)=>Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier),out var id)&&await recipes.IsEditor(id,ct);
    public record SaveInput(string Title,int Revision);
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct)
    { Response.Headers.CacheControl="no-store";return await Editor(ct)?Ok(new { categories=await categories.List(ct) }):Forbid(); }
    [HttpPut("{id}")] public async Task<IActionResult> Save(string id,SaveInput input,CancellationToken ct)
    {
        Response.Headers.CacheControl="no-store";if(!await Editor(ct))return Forbid();
        if(!CategoryStore.ValidId(id)||string.IsNullOrWhiteSpace(input.Title)||input.Title.Trim().Length>80||input.Revision<0||input.Revision==int.MaxValue)return BadRequest();
        try { var revision=await categories.Save(id,input.Title,input.Revision,ct);
          return revision is null?Conflict(new { message="Категория изменена или такой адрес уже существует. Обновите список." }):Ok(new { revision }); }
        catch(PostgresException e) when(e.SqlState=="23505") { return Conflict(new { message="Категория с таким названием уже существует." }); }
    }
    [HttpDelete("{id}")] public async Task<IActionResult> Delete(string id,[FromQuery]int revision,CancellationToken ct)
    {
        Response.Headers.CacheControl="no-store";if(!await Editor(ct))return Forbid();
        if(!CategoryStore.ValidId(id)||revision<1||revision==int.MaxValue)return BadRequest();
        return await categories.Delete(id,revision,ct)?NoContent():Conflict(new { message="Категория используется в рецептах или изменена. Перенесите рецепты в другую категорию и обновите список." });
    }
}
