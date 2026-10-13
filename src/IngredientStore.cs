using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Npgsql;
namespace Nabo.Recipes;
public class IngredientStore(NpgsqlDataSource database)
{
    public static string Key(string name) {
        var key=Regex.Replace(name.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant().Replace('ё','е'),@"\s+"," ");
        return key switch {
          "яйца" or "яйца куриные" or "яйцо куриное" or "яйцо для смазки" or "яйцо в тесто" or "яйцо целиком" => "яйцо",
          "огурцы"=>"огурец", "помидоры"=>"помидор", "лук репчатый"=>"лук", _=>key };
    }
    static string Label(string name)=>Key(name) switch { "яйцо"=>"Яйца куриные","огурец"=>"Огурцы","помидор"=>"Помидоры","лук"=>"Лук репчатый",_=>name.Trim() };
    public static string Id(string name)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key(name)))).ToLowerInvariant();
    static IEnumerable<JsonNode> Rows(JsonNode? doc)=>doc?["recipe"]?["ingredients"]?.AsArray().Where(x=>x is not null).Select(x=>x!) ?? [];
    static string Name(JsonNode row)=>row["name"]?.GetValue<string>()??"";
    public static async Task<JsonElement> RegisterAndApply(JsonElement document,NpgsqlConnection connection,NpgsqlTransaction tx,CancellationToken ct)
    {
        var doc=JsonNode.Parse(document.GetRawText())!;
        foreach(var row in Rows(doc)) {
            var name=Name(row).Trim();if(string.IsNullOrWhiteSpace(name))continue;
            var query=row["searchQuery"]?.GetValue<string>()?.Trim();
            query=string.IsNullOrWhiteSpace(query)?name:query;
            await using var cmd=new NpgsqlCommand("""
              INSERT INTO nabo_recipes.shared_ingredients(id,name_key,name,search_query,revision,shared_override)
              VALUES(@id,@key,@name,@query,1,false) ON CONFLICT(name_key) DO UPDATE SET name_key=EXCLUDED.name_key
              RETURNING search_query,shared_override
              """,connection,tx);
            cmd.Parameters.AddWithValue("id",Id(name));cmd.Parameters.AddWithValue("key",Key(name));cmd.Parameters.AddWithValue("name",Label(name));cmd.Parameters.AddWithValue("query",query);
            await using var reader=await cmd.ExecuteReaderAsync(ct);await reader.ReadAsync(ct);
            if(reader.GetBoolean(1))ApplyRow(doc,row,reader.GetString(0));
        }
        return JsonSerializer.SerializeToElement(doc);
    }
    static void ApplyRow(JsonNode doc,JsonNode row,string query)
    {
        row["searchQuery"]=query;
        var id=row["ingredientId"]?.GetValue<string>();
        foreach(var entry in doc["ingredients"]?.AsArray()??new JsonArray())
            if(entry?["id"]?.GetValue<string>()==id)entry!["searchTerms"]=new JsonArray(JsonValue.Create(query));
    }
    public async Task<object> List(CancellationToken ct)
    {
        var usage=new Dictionary<string,Dictionary<string,string>>();
        var variants=new Dictionary<string,HashSet<string>>();
        await using(var cmd=database.CreateCommand("SELECT slug,draft::text,published::text FROM nabo_recipes.managed_recipes ORDER BY slug")) {
            await using var reader=await cmd.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))for(var col=1;col<=2;col++) {
                if(reader.IsDBNull(col))continue;var doc=JsonNode.Parse(reader.GetString(col));
                foreach(var row in Rows(doc)) {
                    var id=Id(Name(row));if(!usage.ContainsKey(id)){usage[id]=new();variants[id]=new();}
                    usage[id][reader.GetString(0)]=doc?["recipe"]?["title"]?.GetValue<string>()??reader.GetString(0);
                    var query=row["searchQuery"]?.GetValue<string>();if(!string.IsNullOrWhiteSpace(query))variants[id].Add(query);
                }
            }
        }
        await using var list=database.CreateCommand("SELECT id,name,search_query,revision,shared_override FROM nabo_recipes.shared_ingredients ORDER BY name_key");
        await using var rows=await list.ExecuteReaderAsync(ct);var items=new List<object>();
        while(await rows.ReadAsync(ct)) {
            var id=rows.GetString(0);var recipes=usage.GetValueOrDefault(id)??new();
            items.Add(new { id,name=rows.GetString(1),searchQuery=rows.GetString(2),revision=rows.GetInt32(3),shared=rows.GetBoolean(4),
              usageCount=recipes.Count,queries=variants.GetValueOrDefault(id)?.Order().ToArray()??[],recipes=recipes.Select(x=>new {slug=x.Key,title=x.Value}).ToArray() });
        }
        return new { items };
    }
    public async Task<(int Revision,int Affected)?> Save(string id,string query,int revision,CancellationToken ct)
    {
        await using var connection=await database.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);
        await using(var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(113355204,2)",connection,tx))await gate.ExecuteNonQueryAsync(ct);
        string key;int updated;
        await using(var update=new NpgsqlCommand("UPDATE nabo_recipes.shared_ingredients SET search_query=@query,shared_override=true,revision=revision+1 WHERE id=@id AND revision=@revision RETURNING name_key,revision",connection,tx)) {
            update.Parameters.AddWithValue("id",id);update.Parameters.AddWithValue("query",query);update.Parameters.AddWithValue("revision",revision);
            await using var r=await update.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;key=r.GetString(0);updated=r.GetInt32(1);
        }
        var recipes=new List<(string Slug,JsonNode Draft,JsonNode? Published)>();
        await using(var cmd=new NpgsqlCommand("SELECT slug,draft::text,published::text FROM nabo_recipes.managed_recipes FOR UPDATE",connection,tx)) {
            await using var reader=await cmd.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))recipes.Add((reader.GetString(0),JsonNode.Parse(reader.GetString(1))!,reader.IsDBNull(2)?null:JsonNode.Parse(reader.GetString(2))));
        }
        int affected=0;
        foreach(var (slug,draft,published) in recipes) {
            bool found=false;
            foreach(var doc in new[]{draft,published})if(doc is not null)foreach(var row in Rows(doc))
              if(Key(Name(row))==key){ApplyRow(doc,row,query);found=true;}
            if(!found)continue;
            await using var cmd=new NpgsqlCommand("UPDATE nabo_recipes.managed_recipes SET draft=CAST(@draft AS jsonb),published=CAST(@published AS jsonb),revision=revision+1,updated_at=now() WHERE slug=@slug",connection,tx);
            cmd.Parameters.AddWithValue("slug",slug);cmd.Parameters.AddWithValue("draft",draft.ToJsonString());
            cmd.Parameters.Add("published",NpgsqlTypes.NpgsqlDbType.Text).Value=(object?)published?.ToJsonString()??DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);affected++;
        }
        await tx.CommitAsync(ct);return (updated,affected);
    }
}
