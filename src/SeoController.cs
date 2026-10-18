using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
namespace Nabo.Recipes;

[ApiController, Route("api/seo")]
public class SeoController(NpgsqlDataSource database, IConfiguration config) : ControllerBase
{
    public static readonly HashSet<string> Stores = ["evroopt", "green", "gippo", "sosedi", "belmarket", "santa"];
    public record ProductReference(string StoreId, string Id, string Name, string? Unit, string? Image);
    public record IngestRequest(ProductReference[] Items);
    public static bool Valid(ProductReference p) => p is not null && Stores.Contains(p.StoreId) && !string.IsNullOrWhiteSpace(p.Id)
        && p.Id.Length <= 200 && !string.IsNullOrWhiteSpace(p.Name) && p.Name.Length <= 500
        && (p.Unit?.Length ?? 0) <= 200 && (p.Image is null || p.Image.Length <= 2048 &&
          (p.Image.StartsWith("/api/product-image?") || Uri.TryCreate(p.Image, UriKind.Absolute, out var u) && u.Scheme == "https"));
    [HttpPost("products"), RequestSizeLimit(262144)]
    public async Task<IActionResult> Ingest(IngestRequest input, CancellationToken ct)
    {
        var expected = config["SEO_INGEST_KEY"];
        var supplied = Request.Headers["X-Nabo-Seo-Key"].ToString();
        if (string.IsNullOrEmpty(expected) || !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied)))) return Unauthorized();
        if (input.Items is null || input.Items.Length > 200 || input.Items.Any(p => !Valid(p))) return BadRequest();
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        foreach (var p in input.Items.DistinctBy(p => (p.StoreId, p.Id)))
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO nabo_recipes.seo_products(store_id,id,name,unit,image) VALUES(@store,@id,@name,@unit,@image)
                ON CONFLICT(store_id,id) DO UPDATE SET name=excluded.name,unit=excluded.unit,image=excluded.image,
                updated_at=CASE WHEN (seo_products.name,seo_products.unit,seo_products.image) IS DISTINCT FROM
                  (excluded.name,excluded.unit,excluded.image) THEN now() ELSE seo_products.updated_at END,
                last_seen_at=now()
                """, connection, tx);
            cmd.Parameters.AddWithValue("store", p.StoreId);cmd.Parameters.AddWithValue("id", p.Id);
            cmd.Parameters.AddWithValue("name", p.Name);cmd.Parameters.AddWithValue("unit", p.Unit ?? "");
            cmd.Parameters.Add("image", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)p.Image ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Ok(new { saved = input.Items.Length });
    }
    [HttpGet("manifest")]
    public async Task<IActionResult> Manifest(CancellationToken ct)
    {
        await using var cmd = database.CreateCommand("""
          SELECT (SELECT count(*) FROM nabo_recipes.managed_recipes WHERE published IS NOT NULL AND COALESCE((published->'recipe'->>'isActive')::boolean,false)),
          (SELECT count(*) FROM nabo_recipes.seo_products WHERE last_seen_at > now()-interval '30 days')
          """);
        await using var r=await cmd.ExecuteReaderAsync(ct);await r.ReadAsync(ct);
        Response.Headers.CacheControl="public,max-age=60";
        return Ok(new { recipes=r.GetInt64(0),products=r.GetInt64(1) });
    }
    [HttpGet("recipes")]
    public async Task<IActionResult> Recipes([FromQuery] int offset=0,[FromQuery] int limit=2000,CancellationToken ct=default)
    {
        if(offset<0 || limit<1 || limit>2000) return BadRequest();
        await using var cmd=database.CreateCommand("""
          SELECT slug,published->'recipe'->>'title',published->'recipe'->>'image' FROM nabo_recipes.managed_recipes
          WHERE published IS NOT NULL AND COALESCE((published->'recipe'->>'isActive')::boolean,false)
          ORDER BY slug OFFSET @offset LIMIT @limit
          """);cmd.Parameters.AddWithValue("offset",offset);cmd.Parameters.AddWithValue("limit",limit);
        await using var r=await cmd.ExecuteReaderAsync(ct);var items=new List<object>();
        while(await r.ReadAsync(ct)) items.Add(new { slug=r.GetString(0), title=r.GetString(1),image=r.IsDBNull(2)?null:r.GetString(2) });
        Response.Headers.CacheControl="public,max-age=60";return Ok(new { items });
    }
    [HttpGet("products")]
    public async Task<IActionResult> Products([FromQuery] int offset=0,[FromQuery] int limit=2000,[FromQuery] string? storeId=null,[FromQuery] string? id=null,CancellationToken ct=default)
    {
        if(offset<0 || limit<1 || limit>2000 || storeId is not null && !Stores.Contains(storeId)) return BadRequest();
        const string filter="(@id<>'' OR last_seen_at > now()-interval '30 days') AND (@store='' OR store_id=@store) AND (@id='' OR id=@id)";
        await using var count=database.CreateCommand("SELECT count(*) FROM nabo_recipes.seo_products WHERE "+filter);
        count.Parameters.AddWithValue("store",storeId??"");count.Parameters.AddWithValue("id",id??"");
        var total=Convert.ToInt64(await count.ExecuteScalarAsync(ct));
        await using var cmd=database.CreateCommand("SELECT store_id,id,name,unit,image,updated_at FROM nabo_recipes.seo_products WHERE "+filter+" ORDER BY store_id,id OFFSET @offset LIMIT @limit");
        cmd.Parameters.AddWithValue("store",storeId??"");cmd.Parameters.AddWithValue("id",id??"");cmd.Parameters.AddWithValue("offset",offset);cmd.Parameters.AddWithValue("limit",limit);
        await using var r=await cmd.ExecuteReaderAsync(ct);var items=new List<object>();
        while(await r.ReadAsync(ct)) items.Add(new { storeId=r.GetString(0),id=r.GetString(1),name=r.GetString(2),unit=r.GetString(3),image=r.IsDBNull(4)?null:r.GetString(4),updatedAt=r.GetDateTime(5).ToUniversalTime() });
        Response.Headers.CacheControl="public,max-age=60";return Ok(new { total,items });
    }
}

[ApiController,Route("api/recipe-catalog")]
public class PublicRecipesController(NpgsqlDataSource database) : ControllerBase
{
    [HttpGet("{slug}")]
    public async Task<IActionResult> Detail(string slug,CancellationToken ct)
    {
        await using var cmd=database.CreateCommand("SELECT published::text FROM nabo_recipes.managed_recipes WHERE slug=@slug AND published IS NOT NULL AND COALESCE((published->'recipe'->>'isActive')::boolean,false)");
        cmd.Parameters.AddWithValue("slug",slug);var raw=await cmd.ExecuteScalarAsync(ct) as string;
        return raw is null ? NotFound() : Ok(new { document=JsonSerializer.Deserialize<JsonElement>(raw) });
    }
    [HttpGet("page")]
    public async Task<IActionResult> Page([FromQuery] string q="",[FromQuery] string category="",[FromQuery] string collection="",[FromQuery] int offset=0,[FromQuery] int limit=24,CancellationToken ct=default)
    {
        if(offset<0 || limit<1 || limit>100 || q.Length>120 || category.Length>40 || collection.Length>40) return BadRequest();
        var pattern=category switch { "chicken"=>"куриц|курин|цыплен|индейк", "meat"=>"говяд|свинин|баранин|телят|бекон|ветчин|колбас|мясн|фарш", "fish"=>"рыб|лосос|семг|треск|тунец|тунц|форел|сельдь|сельди|сельдью|скумбр|сардин|анчоус|кревет|мидии|кальмар|морепродукт", _=>"a^" };
        const string filter="""
          published IS NOT NULL AND COALESCE((published->'recipe'->>'isActive')::boolean,false)
          AND (@category='' OR published->'recipe'->>'categoryId'=@category OR (published->'recipe'->'tags') ? @category
            OR lower(COALESCE(published->'recipe'->>'title','') || (published->'recipe'->'ingredients')::text) ~ @pattern)
          AND CASE @collection
            WHEN 'quick20' THEN (published->'recipe'->>'cookingTime')::numeric<=20
            WHEN 'quick30' THEN (published->'recipe'->>'cookingTime')::numeric<=30
            WHEN 'family' THEN (published->'recipe'->'tags') ? 'family'
            WHEN 'easy' THEN published->'recipe'->>'difficulty'='easy' OR (published->'recipe'->'tags') ? 'easy'
            WHEN 'budget' THEN (published->'recipe'->'tags') ? 'budget'
            WHEN 'protein' THEN (published->'recipe'->'nutrition'->>'protein')::numeric *
              CASE published->'recipe'->'nutrition'->>'basis' WHEN 'serving' THEN 1 WHEN 'recipe' THEN 1/NULLIF((published->'recipe'->>'servings')::numeric,0)
              WHEN '100g' THEN (published->'recipe'->>'yieldGrams')::numeric/100/NULLIF((published->'recipe'->>'servings')::numeric,0) END >=25
            WHEN 'light' THEN (published->'recipe'->'nutrition'->>'calories')::numeric *
              CASE published->'recipe'->'nutrition'->>'basis' WHEN 'serving' THEN 1 WHEN 'recipe' THEN 1/NULLIF((published->'recipe'->>'servings')::numeric,0)
              WHEN '100g' THEN (published->'recipe'->>'yieldGrams')::numeric/100/NULLIF((published->'recipe'->>'servings')::numeric,0) END <=500
            ELSE true END
          AND (@q='' OR lower(COALESCE(published->'recipe'->>'title','') || COALESCE(published->'recipe'->>'description','') || (published->'recipe'->'ingredients')::text || (published->'ingredients')::text) LIKE @search)
          """;
        void Params(NpgsqlCommand cmd) { cmd.Parameters.AddWithValue("category",category);cmd.Parameters.AddWithValue("collection",collection);cmd.Parameters.AddWithValue("q",q);cmd.Parameters.AddWithValue("search","%"+q.ToLowerInvariant()+"%");cmd.Parameters.AddWithValue("pattern",pattern); }
        await using var count=database.CreateCommand("SELECT count(*) FROM nabo_recipes.managed_recipes WHERE "+filter);Params(count);
        var total=Convert.ToInt64(await count.ExecuteScalarAsync(ct));
        await using var cmd=database.CreateCommand("SELECT published::text FROM nabo_recipes.managed_recipes WHERE "+filter+" ORDER BY slug OFFSET @offset LIMIT @limit");Params(cmd);cmd.Parameters.AddWithValue("offset",offset);cmd.Parameters.AddWithValue("limit",limit);
        await using var r=await cmd.ExecuteReaderAsync(ct);var documents=new List<JsonElement>();
        while(await r.ReadAsync(ct)) documents.Add(JsonSerializer.Deserialize<JsonElement>(r.GetString(0)));
        return Ok(new { total,documents });
    }
}
