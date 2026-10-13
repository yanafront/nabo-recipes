using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using System.Text.RegularExpressions;
namespace Nabo.Recipes;
[ApiController,Route("api/admin/ingredients"),Authorize]
public class IngredientController(IngredientStore ingredients,RecipeStore recipes):ControllerBase
{
    async Task<bool> Editor(CancellationToken ct)=>Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier),out var id)&&await recipes.IsEditor(id,ct);
    [HttpGet]public async Task<IActionResult> List(CancellationToken ct){Response.Headers.CacheControl="no-store";return await Editor(ct)?Ok(await ingredients.List(ct)):Forbid();}
    public record SaveInput(string SearchQuery,int Revision);
    [HttpPut("{id}")]public async Task<IActionResult> Save(string id,SaveInput input,CancellationToken ct){
        Response.Headers.CacheControl="no-store";if(!await Editor(ct))return Forbid();
        if(!Regex.IsMatch(id,"^[a-f0-9]{64}$")||string.IsNullOrWhiteSpace(input.SearchQuery)||input.SearchQuery.Trim().Length>160||input.Revision<1||input.Revision==int.MaxValue)return BadRequest();
        var result=await ingredients.Save(id,input.SearchQuery.Trim(),input.Revision,ct);
        return result is null?Conflict(new {message="Ингредиент изменён в другой вкладке. Обновите список."}):Ok(new {revision=result.Value.Revision,affectedRecipes=result.Value.Affected});
    }
}
