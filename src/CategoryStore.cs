using Npgsql;
using System.Text.RegularExpressions;
namespace Nabo.Recipes;
public record RecipeCategory(string Id, string Title, int Revision);
public class CategoryStore(NpgsqlDataSource database)
{
    public static readonly (string Id,string Title)[] Defaults = [
      ("breakfast","Завтраки"),("soups","Супы"),("mains","Основные блюда"),("salads","Салаты"),
      ("pasta","Паста"),("chicken","Курица"),("meat","Мясо"),("fish","Рыба"),("sides","Гарниры"),("baking","Выпечка"),("desserts","Десерты")];
    public static bool ValidId(string? id) => id is not null && id.Length<=40 && Regex.IsMatch(id,"^[a-z0-9]+(?:-[a-z0-9]+)*$");
    public async Task<List<RecipeCategory>> List(CancellationToken ct)
    {
        await using var cmd=database.CreateCommand("SELECT id,title,revision FROM nabo_recipes.recipe_categories ORDER BY sort_order,id");
        await using var reader=await cmd.ExecuteReaderAsync(ct);var rows=new List<RecipeCategory>();
        while(await reader.ReadAsync(ct)) rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetInt32(2)));
        return rows;
    }
    public async Task<int?> Save(string id,string title,int revision,CancellationToken ct)
    {
        await using var connection=await database.OpenConnectionAsync(ct);
        await using var tx=await connection.BeginTransactionAsync(ct);
        await using(var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(113355204,2)",connection,tx)) await gate.ExecuteNonQueryAsync(ct);
        await using var cmd=new NpgsqlCommand(revision==0 ? """
          INSERT INTO nabo_recipes.recipe_categories(id,title,revision,sort_order)
          SELECT @id,@title,1,COALESCE(MAX(sort_order),0)+1 FROM nabo_recipes.recipe_categories
          HAVING COUNT(*)<100 ON CONFLICT(id) DO NOTHING RETURNING revision
          """ : "UPDATE nabo_recipes.recipe_categories SET title=@title,revision=revision+1 WHERE id=@id AND revision=@revision RETURNING revision",connection,tx);
        cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("title",title.Trim());cmd.Parameters.AddWithValue("revision",revision);
        var value=await cmd.ExecuteScalarAsync(ct);await tx.CommitAsync(ct);return value is null ? null : Convert.ToInt32(value);
    }
    public async Task<bool> Delete(string id,int revision,CancellationToken ct)
    {
        await using var connection=await database.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);
        await using(var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(113355204,2)",connection,tx)) await gate.ExecuteNonQueryAsync(ct);
        await using var cmd=new NpgsqlCommand("""
          DELETE FROM nabo_recipes.recipe_categories WHERE id=@id AND revision=@revision
          AND NOT EXISTS(SELECT 1 FROM nabo_recipes.managed_recipes WHERE draft->'recipe'->>'categoryId'=@id OR published->'recipe'->>'categoryId'=@id)
          """,connection,tx);
        cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("revision",revision);
        var deleted=await cmd.ExecuteNonQueryAsync(ct)==1;await tx.CommitAsync(ct);return deleted;
    }
}
