using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
namespace Nabo.Recipes;

public class RecipeStore(NpgsqlDataSource database)
{
    public static IEnumerable<JsonElement> SeedDocuments()
    {
        using var stream = typeof(RecipeStore).Assembly.GetManifestResourceStream("Nabo.Recipes.Data.catalog.json")!;
        var catalog = JsonNode.Parse(stream)!;
        foreach (var recipe in catalog["recipes"]!.AsArray())
        {
            var ids = recipe!["ingredients"]!.AsArray().Select(i => i!["ingredientId"]!.GetValue<string>()).ToHashSet();
            yield return JsonSerializer.SerializeToElement(new JsonObject
            {
                ["recipe"] = recipe.DeepClone(),
                ["ingredients"] = new JsonArray(catalog["ingredients"]!.AsArray()
                    .Where(i => ids.Contains(i!["id"]!.GetValue<string>())).Select(i => i!.DeepClone()).ToArray())
            });
        }
    }
    public async Task Initialize(string? phone, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var schema = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(113355204, 1);
            CREATE SCHEMA IF NOT EXISTS nabo_recipes;
            CREATE TABLE IF NOT EXISTS nabo_recipes.seo_products(
              store_id varchar(30) NOT NULL,id varchar(200) NOT NULL,name varchar(500) NOT NULL,unit varchar(200) NOT NULL DEFAULT '',image text,
              updated_at timestamptz NOT NULL DEFAULT now(),last_seen_at timestamptz NOT NULL DEFAULT now(),PRIMARY KEY(store_id,id));
            CREATE INDEX IF NOT EXISTS seo_products_seen ON nabo_recipes.seo_products(last_seen_at);

            CREATE TABLE IF NOT EXISTS nabo_recipes.schema_versions(version integer PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS nabo_recipes.recipe_categories(id varchar(40) PRIMARY KEY,title varchar(80) NOT NULL,revision integer NOT NULL CHECK(revision>0),sort_order integer NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS recipe_categories_title ON nabo_recipes.recipe_categories(lower(title));
            CREATE TABLE IF NOT EXISTS nabo_recipes.recipe_editors(user_id uuid PRIMARY KEY, created_at timestamptz NOT NULL DEFAULT now());
            CREATE TABLE IF NOT EXISTS nabo_recipes.managed_recipes(
              slug varchar(100) PRIMARY KEY, draft jsonb NOT NULL, published jsonb,
              revision integer NOT NULL CHECK(revision>0), updated_at timestamptz NOT NULL DEFAULT now());
            CREATE INDEX IF NOT EXISTS managed_recipes_public_slug ON nabo_recipes.managed_recipes(slug)
              WHERE published IS NOT NULL AND COALESCE((published->'recipe'->>'isActive')::boolean,false);
            """, connection, transaction);
        await schema.ExecuteNonQueryAsync(ct);
        await using var version = new NpgsqlCommand("SELECT count(*) FROM nabo_recipes.schema_versions WHERE version=1", connection, transaction);
        if (Convert.ToInt64(await version.ExecuteScalarAsync(ct)) == 0)
        {
            foreach (var document in SeedDocuments())
            {
                var slug = document.GetProperty("recipe").GetProperty("slug").GetString()!;
                if (!RecipeValidation.Valid(slug, document)) throw new InvalidOperationException("Invalid recipe seed: " + slug);
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO nabo_recipes.managed_recipes(slug,draft,published,revision)
                    VALUES(@slug,CAST(@doc AS jsonb),CAST(@doc AS jsonb),1) ON CONFLICT(slug) DO NOTHING
                    """, connection, transaction);
                seed.Parameters.AddWithValue("slug", slug); seed.Parameters.AddWithValue("doc", document.GetRawText());
                await seed.ExecuteNonQueryAsync(ct);
            }
            await using var mark = new NpgsqlCommand("INSERT INTO nabo_recipes.schema_versions VALUES(1)", connection, transaction);
            await mark.ExecuteNonQueryAsync(ct);
        }
        await using(var categoryVersion=new NpgsqlCommand("SELECT count(*) FROM nabo_recipes.schema_versions WHERE version=2",connection,transaction))
        if(Convert.ToInt64(await categoryVersion.ExecuteScalarAsync(ct))==0) {
            foreach(var (categoryId,title) in CategoryStore.Defaults) {
                await using var cmd=new NpgsqlCommand("INSERT INTO nabo_recipes.recipe_categories(id,title,revision,sort_order) VALUES(@id,@title,1,@order) ON CONFLICT(id) DO NOTHING",connection,transaction);
                cmd.Parameters.AddWithValue("id",categoryId);cmd.Parameters.AddWithValue("title",title);cmd.Parameters.AddWithValue("order",Array.FindIndex(CategoryStore.Defaults,c=>c.Id==categoryId));await cmd.ExecuteNonQueryAsync(ct);
            }
            await using var mark=new NpgsqlCommand("INSERT INTO nabo_recipes.schema_versions VALUES(2)",connection,transaction);await mark.ExecuteNonQueryAsync(ct);
        }
        if (!string.IsNullOrWhiteSpace(phone))
        {
            // Bootstrap only once, against an existing user. Do not transfer grants on phone reuse.
            await using var grant = new NpgsqlCommand("""
                INSERT INTO nabo_recipes.recipe_editors(user_id)
                SELECT id FROM public.users WHERE phone_number=@phone
                  AND NOT EXISTS(SELECT 1 FROM nabo_recipes.recipe_editors)
                ON CONFLICT DO NOTHING
                """, connection, transaction);
            grant.Parameters.AddWithValue("phone", phone);
            var granted = await grant.ExecuteNonQueryAsync(ct);
            if (granted == 0)
            {
                await using var count = new NpgsqlCommand("SELECT count(*) FROM nabo_recipes.recipe_editors", connection, transaction);
                if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) == 0)
                    throw new InvalidOperationException("Register the configured editor in Nabo before deployment.");
            }
        }
        await transaction.CommitAsync(ct);
    }
    public async Task Check(CancellationToken ct)
    {
        await using var cmd = database.CreateCommand("SELECT count(*) FROM nabo_recipes.schema_versions WHERE version=1");
        if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) != 1) throw new InvalidOperationException("Schema not initialized.");
    }
    public async Task<bool> IsEditor(Guid id, CancellationToken ct)
    {
        await using var cmd = database.CreateCommand("SELECT count(*) FROM nabo_recipes.recipe_editors WHERE user_id=@id");
        cmd.Parameters.AddWithValue("id", id);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 1;
    }
    public async Task<object> List(CancellationToken ct)
    {
        await using var cmd = database.CreateCommand("SELECT slug,draft::text,revision,published IS NOT NULL FROM nabo_recipes.managed_recipes ORDER BY updated_at DESC,slug");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var records = new List<object>();
        while (await reader.ReadAsync(ct)) records.Add(new { slug = reader.GetString(0), document = JsonSerializer.Deserialize<JsonElement>(reader.GetString(1)), revision = reader.GetInt32(2), published = reader.GetBoolean(3) });
        await reader.DisposeAsync();
        return new { records, categories = await new CategoryStore(database).List(ct) };
    }
    public async Task<object> Published(CancellationToken ct)
    {
        await using var cmd = database.CreateCommand("SELECT published::text FROM nabo_recipes.managed_recipes WHERE published IS NOT NULL ORDER BY slug");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var documents = new List<JsonElement>();
        while (await reader.ReadAsync(ct)) documents.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
        return new { documents };
    }
    public async Task<bool> HasCategory(string category,CancellationToken ct) {
        await using var cmd=database.CreateCommand("SELECT EXISTS(SELECT 1 FROM nabo_recipes.recipe_categories WHERE id=@id)");cmd.Parameters.AddWithValue("id",category);return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
    public async Task<int?> Save(string slug, JsonElement document, int revision, bool publish, CancellationToken ct)
    {
        await using var connection=await database.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);
        await using(var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(113355204,2)",connection,tx)) await gate.ExecuteNonQueryAsync(ct);
        if(document.TryGetProperty("recipe",out var recipe)) {
            await using var exists=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM nabo_recipes.recipe_categories WHERE id=@id)",connection,tx);
            exists.Parameters.AddWithValue("id",recipe.GetProperty("categoryId").GetString()!);if(!(bool)(await exists.ExecuteScalarAsync(ct))!)return null;
        }
        await using var cmd = new NpgsqlCommand(revision == 0 ? """
            INSERT INTO nabo_recipes.managed_recipes(slug,draft,published,revision)
            VALUES(@slug,CAST(@doc AS jsonb),CASE WHEN @publish THEN CAST(@doc AS jsonb) ELSE NULL END,1)
            ON CONFLICT(slug) DO NOTHING RETURNING revision
            """ : """
            UPDATE nabo_recipes.managed_recipes SET draft=CAST(@doc AS jsonb),
            published=CASE WHEN @publish THEN CAST(@doc AS jsonb) ELSE published END,
            revision=revision+1,updated_at=now() WHERE slug=@slug AND revision=@revision RETURNING revision
            """,connection,tx);
        cmd.Parameters.AddWithValue("slug", slug); cmd.Parameters.AddWithValue("doc", document.GetRawText());
        cmd.Parameters.AddWithValue("publish", publish); cmd.Parameters.AddWithValue("revision", revision);
        var result = await cmd.ExecuteScalarAsync(ct);
        await tx.CommitAsync(ct);
        return result is null ? null : Convert.ToInt32(result);
    }
    public async Task<int?> SetCategory(string slug, string category, int revision, CancellationToken ct)
    {
        await using var connection=await database.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);
        await using(var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(113355204,2)",connection,tx)) await gate.ExecuteNonQueryAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            UPDATE nabo_recipes.managed_recipes SET
            draft=jsonb_set(draft,'{recipe,categoryId}',to_jsonb(CAST(@category AS text))),
            published=CASE WHEN published IS NULL THEN NULL ELSE jsonb_set(published,'{recipe,categoryId}',to_jsonb(CAST(@category AS text))) END,
            revision=revision+1,updated_at=now() WHERE slug=@slug AND revision=@revision
            AND EXISTS(SELECT 1 FROM nabo_recipes.recipe_categories WHERE id=@category) RETURNING revision
            """,connection,tx);
        cmd.Parameters.AddWithValue("slug",slug); cmd.Parameters.AddWithValue("category",category); cmd.Parameters.AddWithValue("revision",revision);
        var result=await cmd.ExecuteScalarAsync(ct);await tx.CommitAsync(ct);
        return result is null ? null : Convert.ToInt32(result);
    }
    public async Task<bool> Delete(string slug, int revision, CancellationToken ct)
    {
        await using var cmd = database.CreateCommand("DELETE FROM nabo_recipes.managed_recipes WHERE slug=@slug AND revision=@revision");
        cmd.Parameters.AddWithValue("slug", slug);
        cmd.Parameters.AddWithValue("revision", revision);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

}
