using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication;
using System.Net.Http.Headers;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nabo.Recipes;
using Npgsql;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }
Check(SeoController.Valid(new("evroopt", "123", "Молоко", "1 л", "https://example.com/photo.jpg")), "valid SEO product reference");
Check(!SeoController.Valid(new("unknown", "123", "Молоко", null, null)), "reject unknown SEO store");
Check(!SeoController.Valid(new("evroopt", "123", "Молоко", null, "javascript:alert(1)")), "reject unsafe SEO image");
Check(IngredientStore.Id("  Яйца ")==IngredientStore.Id("Яйцо для смазки"),"safe egg variants deduplicate");
Check(IngredientStore.Id("Зелёный   лук")==IngredientStore.Id("зеленый лук"),"ingredient normalization ignores case spaces and yo");
Check(IngredientStore.Id("Яйцо шоколадное")!=IngredientStore.Id("Яйца"),"different products never merge blindly");
Check(RecipeClassification.ValidCollections(new(["family"],["easy"])),"classification accepts supported flags");
Check(!RecipeClassification.ValidCollections(new(["family"],["family"])),"classification rejects overlapping additions and removals");
Check(!RecipeClassification.ValidCollections(new(["unknown"],[])),"classification rejects unknown flags");
Check(!RecipeClassification.ValidCollections(new(null!,[])),"classification rejects null flag lists");
var seeds = RecipeStore.SeedDocuments().ToArray();
Check(seeds.Length == 41, "preserve existing 41 recipes");
foreach (var doc in seeds) Check(RecipeValidation.Valid(doc.GetProperty("recipe").GetProperty("slug").GetString()!, doc), "valid seed");
var edited = JsonNode.Parse(seeds[0].GetRawText())!;
var slug = edited["recipe"]!["slug"]!.GetValue<string>();
bool Valid() => RecipeValidation.Valid(slug, JsonSerializer.SerializeToElement(edited));
edited["recipe"]!["ingredients"]![0]!["searchQuery"] = "Яйца куриные";
edited["recipe"]!["ingredients"]![0]!["exactName"] = "Яйца куриные С1 10 шт";
Check(Valid(), "exact retail name accepted");
edited["recipe"]!["ingredients"]![0]!["searchQuery"] = "";
Check(!Valid(), "empty search rejected");
edited["recipe"]!["ingredients"]![0]!["searchQuery"] = "Яйца куриные";
edited["recipe"]!["image"] = "javascript:alert(1)";
Check(!Valid(), "unsafe image rejected");
Check(!RecipeValidation.Valid("bad/slug", default), "malformed document rejected");
Check(typeof(RecipeController).GetCustomAttribute<AuthorizeAttribute>() is not null, "admin endpoints require authentication");
Check(typeof(RecipeController).GetMethod("Delete")!.GetCustomAttribute<HttpDeleteAttribute>()!.Template == "{slug}", "recipe deletion has a dedicated DELETE endpoint");
Check(RemoteIdentity.Parse(JsonSerializer.SerializeToElement(new { phoneNumber = "+375295495775" })) is null, "phone alone cannot authorize");
Check(RemoteIdentity.Parse(JsonSerializer.SerializeToElement(new { id = Guid.Empty })) is null, "empty identity rejected");
var id = Guid.NewGuid();
Check(RemoteIdentity.Parse(JsonSerializer.SerializeToElement(new { id }))!.FindFirstValue(ClaimTypes.NameIdentifier) == id.ToString(), "remote identity preserves user ID");
var cs = new NpgsqlConnectionStringBuilder(DatabaseConnection.Parse("postgresql://admin:pass%3Aword@db.railway.internal:5432/shared?sslmode=require"));
Check(cs.Password == "pass:word" && cs.Database == "shared" && cs.SslMode == SslMode.Require, "Railway URL parsed without credential corruption");
// Exercise the actual authentication handler with a fake primary API.
async Task<int> AuthStatus(string? bearer, HttpStatusCode upstreamStatus, object profile)
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseTestServer();
    builder.Services.AddLogging();
    builder.Services.AddSingleton<IHttpClientFactory>(new FakeClients(upstreamStatus, JsonSerializer.Serialize(profile)));
    builder.Services.AddAuthentication("Nabo").AddScheme<AuthenticationSchemeOptions, NaboAuthentication>("Nabo", null);
    builder.Services.AddAuthorization();
    await using var app = builder.Build();
    app.UseExceptionHandler(error => error.Run(ctx => { ctx.Response.StatusCode = 503; return Task.CompletedTask; }));
    app.UseAuthentication(); app.UseAuthorization();
    app.MapGet("/protected", () => "ok").RequireAuthorization();
    await app.StartAsync();
    using var client = app.GetTestClient();
    if (bearer is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
    using var response = await client.GetAsync("/protected");
    return (int)response.StatusCode;
}
Check(await AuthStatus(null, HttpStatusCode.OK, new { id }) == 401, "guest challenged");
Check(await AuthStatus("expired", HttpStatusCode.Unauthorized, new { id }) == 401, "expired token rejected");
Check(await AuthStatus("valid", HttpStatusCode.OK, new { id }) == 200, "primary API identity accepted");
Check(await AuthStatus("spoof", HttpStatusCode.OK, new { phoneNumber = "+375295495775" }) == 401, "phone profile cannot bypass identity");
Check(await AuthStatus("valid", HttpStatusCode.ServiceUnavailable, new { id }) == 503, "primary outage fails closed");
// Optional real PostgreSQL integration. Only run against a disposable database.
var database = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");
if (database is not null)
{
    await using var source = NpgsqlDataSource.Create(DatabaseConnection.Parse(database));
    await using (var clean = source.CreateCommand("DROP SCHEMA IF EXISTS nabo_recipes CASCADE")) await clean.ExecuteNonQueryAsync();
    var store = new RecipeStore(source);
    await store.Initialize(null, default);
    Check(await store.Save("test-draft", JsonSerializer.SerializeToElement(new { test = 1 }), 0, false, default) == 1, "create draft");
    Check(await store.Save("test-draft", JsonSerializer.SerializeToElement(new { test = 2 }), 0, true, default) is null, "duplicate create conflicts");
    Check(await store.Save("test-draft", JsonSerializer.SerializeToElement(new { test = 2 }), 1, true, default) == 2, "publish atomically");
    Check(await store.Save("test-draft", JsonSerializer.SerializeToElement(new { test = 3 }), 1, false, default) is null, "stale revision conflicts");
    Check(await store.Save("test-draft", JsonSerializer.SerializeToElement(new { test = 3 }), 2, false, default) == 3, "draft update");
    Check(JsonSerializer.Serialize(await store.Published(default)).Contains("\"test\":2"), "draft does not replace published version");
    await store.Initialize(null, default);
    Check(await store.Save("test-draft", JsonSerializer.SerializeToElement(new { test = 4 }), 3, false, default) == 4, "restart preserves edited data");
    Check(!await store.Delete("test-draft", 3, default), "stale deletion does not remove updated recipe");
    Check(await store.Delete("test-draft", 4, default), "delete current recipe");
    await store.Initialize(null, default);
    Check(!JsonSerializer.Serialize(await store.List(default)).Contains("test-draft"), "deleted recipe does not reappear after restart");
    Check(!await store.IsEditor(id, default), "ordinary user is not an editor");
    var apiBuilder = WebApplication.CreateBuilder();
    apiBuilder.WebHost.UseTestServer();
    apiBuilder.Services.AddSingleton(store);
    apiBuilder.Services.AddSingleton<CategoryStore>();
    apiBuilder.Services.AddSingleton<IngredientStore>();
    apiBuilder.Services.AddSingleton(source);
    apiBuilder.Configuration["SEO_INGEST_KEY"]="isolated-test-key";
    apiBuilder.Services.AddSingleton<IHttpClientFactory>(new FakeClients(HttpStatusCode.OK, JsonSerializer.Serialize(new { id })));
    apiBuilder.Services.AddAuthentication("Nabo").AddScheme<AuthenticationSchemeOptions, NaboAuthentication>("Nabo", null);
    apiBuilder.Services.AddAuthorization();
    apiBuilder.Services.AddControllers().AddApplicationPart(typeof(RecipeController).Assembly);
    await using var api = apiBuilder.Build();
    api.UseAuthentication(); api.UseAuthorization(); api.MapControllers();
    await api.StartAsync();
    using var guest = api.GetTestClient();
    Check((int)(await guest.GetAsync("/api/admin/recipes")).StatusCode == 401, "guest cannot read drafts");
    using var editor = api.GetTestClient();
    editor.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fake-valid-session");
    Check((int)(await editor.GetAsync("/api/admin/recipes")).StatusCode == 403, "ordinary user cannot read drafts");
    Check((int)(await editor.PatchAsJsonAsync("/api/admin/recipes/unknown/classification",new { categoryId="soups",revision=1 })).StatusCode==403,"ordinary user cannot classify recipes");
    await using (var grant = source.CreateCommand("INSERT INTO nabo_recipes.recipe_editors(user_id) VALUES(@id)"))
    {
        grant.Parameters.AddWithValue("id", id); await grant.ExecuteNonQueryAsync();
    }
    Check((int)(await guest.PostAsJsonAsync("/api/seo/products",new { items=new[]{new {storeId="evroopt",id="123",name="Молоко",unit="1 л",image=(string?)null}} })).StatusCode==401,"SEO ingestion requires a server key");
    guest.DefaultRequestHeaders.Add("X-Nabo-Seo-Key","isolated-test-key");
    Check((await guest.PostAsJsonAsync("/api/seo/products",new { items=new[]{new {storeId="evroopt",id="123",name="Молоко",unit="1 л",image=(string?)null}} })).IsSuccessStatusCode,"persist known product metadata");
    Check((await guest.GetStringAsync("/api/seo/products?storeId=evroopt")).Contains("123"),"public product discovery page");
    using(var prefix=JsonDocument.Parse(await guest.GetStringAsync("/api/seo/products?q=%D0%BC%D0%BE%D0%BB"))) Check(prefix.RootElement.GetProperty("total").GetInt64()==1,"SEO product prefix matches case-insensitively");
    using(var prefix=JsonDocument.Parse(await guest.GetStringAsync("/api/seo/products?q=%25"))) Check(prefix.RootElement.GetProperty("total").GetInt64()==0,"SEO product prefix is literal, not a SQL wildcard");
    await using(var expire=source.CreateCommand("UPDATE nabo_recipes.seo_products SET last_seen_at=now()-interval '31 days' WHERE store_id='evroopt' AND id='123'")) await expire.ExecuteNonQueryAsync();
    using(var expired=JsonDocument.Parse(await guest.GetStringAsync("/api/seo/products?storeId=evroopt"))) Check(expired.RootElement.GetProperty("total").GetInt64()==0,"stale product excluded from sitemap discovery");
    Check((await guest.GetStringAsync("/api/seo/products?storeId=evroopt&id=123")).Contains("123"),"known SKU metadata remains available for a fresh lookup");
    Check((await guest.GetAsync("/api/seo/manifest")).IsSuccessStatusCode,"sitemap manifest counts");
    Check((await guest.GetAsync("/api/seo/recipes?limit=2")).IsSuccessStatusCode,"bounded sitemap recipe records");
    Check((await guest.GetAsync("/api/recipe-catalog/page?limit=2")).IsSuccessStatusCode,"bounded public recipe page");
    using(var quick=JsonDocument.Parse(await guest.GetStringAsync("/api/recipe-catalog/page?collection=quick30&limit=100"))) {
      Check(quick.RootElement.GetProperty("documents").GetArrayLength()>0,"quick collection does not depend on stored tags");
      Check(quick.RootElement.GetProperty("documents").EnumerateArray().All(d=>d.GetProperty("recipe").GetProperty("cookingTime").GetDouble()<=30),"quick collection preserves cooking time filter");
    }
    using(var family=JsonDocument.Parse(await guest.GetStringAsync("/api/recipe-catalog/page?collection=family&limit=100"))) Check(family.RootElement.GetProperty("documents").EnumerateArray().All(d=>d.GetProperty("recipe").GetProperty("servings").GetDouble()>=4),"family collection preserves serving filter");
    using(var fish=JsonDocument.Parse(await guest.GetStringAsync("/api/recipe-catalog/page?category=fish&limit=100"))) {
      Check(!fish.RootElement.GetProperty("documents").EnumerateArray().Any(d=>d.GetProperty("recipe").GetProperty("slug").GetString()=="erwtensoep"),"celery never classifies pea soup as fish");
      Check(fish.RootElement.GetProperty("documents").EnumerateArray().Any(d=>d.GetProperty("recipe").GetProperty("slug").GetString()=="fish-and-chips"),"fish category still finds actual fish");
    }
    Check((int)(await guest.GetAsync("/api/recipe-catalog/missing-slug")).StatusCode==404,"unknown recipe has real 404");
    Check((await editor.GetAsync("/api/admin/recipes")).IsSuccessStatusCode, "editor reads catalog");
    Check((int)(await guest.GetAsync("/api/admin/ingredients")).StatusCode==401,"ingredient registry is editor only");
    var eggId=IngredientStore.Id("Яйцо");
    using(var allIngredients=JsonDocument.Parse(await editor.GetStringAsync("/api/admin/ingredients"))) {
        var items=allIngredients.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Check(items.Select(i=>i.GetProperty("id").GetString()).Distinct().Count()==items.Length,"migrated ingredients have no duplicate keys");
        Check(items.Single(i=>i.GetProperty("id").GetString()==eggId).GetProperty("usageCount").GetInt32()>1,"ingredient references all matching recipes");
    }
    var syrnikiBefore=JsonNode.Parse(await guest.GetStringAsync("/api/recipe-catalog/syrniki"))!["document"]!;
    var originalQuantity=syrnikiBefore["recipe"]!["ingredients"]!.AsArray().First(i=>IngredientStore.Key(i!["name"]!.GetValue<string>())=="яйцо")!["quantity"]!.ToJsonString();
    Check((int)(await guest.PutAsJsonAsync("/api/admin/ingredients/"+eggId,new { searchQuery="Яйца куриные С1",revision=1 })).StatusCode==401,"guest cannot change global ingredient query");
    Check((await editor.PutAsJsonAsync("/api/admin/ingredients/"+eggId,new { searchQuery="Яйца куриные С1",revision=1 })).IsSuccessStatusCode,"global query updates related recipes");
    var syrnikiAfter=JsonNode.Parse(await guest.GetStringAsync("/api/recipe-catalog/syrniki"))!["document"]!;
    var eggRow=syrnikiAfter["recipe"]!["ingredients"]!.AsArray().First(i=>IngredientStore.Key(i!["name"]!.GetValue<string>())=="яйцо")!;
    Check(eggRow["searchQuery"]!.GetValue<string>()=="Яйца куриные С1","published recipe uses global query");
    Check(eggRow["quantity"]!.ToJsonString()==originalQuantity,"global ingredient update preserves quantity");
    Check(syrnikiAfter["recipe"]!["instructions"]!.ToJsonString()==syrnikiBefore["recipe"]!["instructions"]!.ToJsonString(),"global query preserves instructions");
    Check((int)(await editor.PutAsJsonAsync("/api/admin/ingredients/"+eggId,new { searchQuery="Other",revision=1 })).StatusCode==409,"stale global query conflicts");
    var sharedDocument=JsonNode.Parse(seeds[0].GetRawText())!;
    sharedDocument["recipe"]!["slug"]="shared-test";
    sharedDocument["recipe"]!["ingredients"]!.AsArray().Add(new JsonObject { ["name"]="Яйца",["ingredientId"]="shared-egg",["quantity"]=2,["unit"]="piece",["searchQuery"]="Старый локальный запрос" });
    sharedDocument["ingredients"]!.AsArray().Add(new JsonObject { ["id"]="shared-egg",["name"]="Яйца",["searchTerms"]=new JsonArray("Старый локальный запрос"),["aliases"]=new JsonArray(),["pantry"]=false,["common"]=true });
    Check((await editor.PutAsJsonAsync("/api/admin/recipes/shared-test",new {document=sharedDocument,revision=0,publish=true})).IsSuccessStatusCode,"new recipe accepts shared ingredient");
    var sharedPublished=await guest.GetStringAsync("/api/recipe-catalog/shared-test");
    var savedShared=JsonNode.Parse(sharedPublished)!["document"]!;
    Check(savedShared["recipe"]!["ingredients"]!.AsArray().Where(i=>IngredientStore.Key(i!["name"]!.GetValue<string>())=="яйцо").All(i=>i!["searchQuery"]!.GetValue<string>()=="Яйца куриные С1"),"later recipe save respects global override");

    Check((int)(await guest.PutAsJsonAsync("/api/admin/recipe-categories/snacks",new { title="Закуски",revision=0 })).StatusCode==401,"guest cannot create categories");
    Check((await editor.PutAsJsonAsync("/api/admin/recipe-categories/snacks",new { title="Закуски",revision=0 })).IsSuccessStatusCode,"editor creates category");
    Check((int)(await editor.PutAsJsonAsync("/api/admin/recipe-categories/snacks",new { title="Дубликат",revision=0 })).StatusCode==409,"duplicate category slug conflicts");
    Check((int)(await editor.PutAsJsonAsync("/api/admin/recipe-categories/duplicate",new { title="Закуски",revision=0 })).StatusCode==409,"duplicate category title conflicts");
    Check((await editor.PutAsJsonAsync("/api/admin/recipe-categories/snacks",new { title="Закуски и снеки",revision=1 })).IsSuccessStatusCode,"rename preserves category slug");
    Check((await guest.GetStringAsync("/api/recipe-categories")).Contains("snacks"),"public dynamic taxonomy includes new category");
    var customDocument=JsonNode.Parse(seeds[0].GetRawText())!;
    customDocument["recipe"]!["slug"]="taxonomy-test";customDocument["recipe"]!["categoryId"]="snacks";
    Check((await editor.PutAsJsonAsync("/api/admin/recipes/taxonomy-test",new { document=customDocument,revision=0,publish=false })).IsSuccessStatusCode,"recipe accepts new category");
    Check((int)(await editor.DeleteAsync("/api/admin/recipe-categories/snacks?revision=2")).StatusCode==409,"category used by draft cannot be deleted");
    Check((await editor.PutAsJsonAsync("/api/admin/recipes/taxonomy-test",new { document=customDocument,revision=1,publish=true })).IsSuccessStatusCode,"recipe publishes in new category");
    Check((int)(await editor.DeleteAsync("/api/admin/recipe-categories/snacks?revision=2")).StatusCode==409,"category used by published recipe cannot be deleted");
    Check((await editor.PatchAsJsonAsync("/api/admin/recipes/taxonomy-test/category",new { categoryId="soups",revision=2 })).IsSuccessStatusCode,"move recipe to another category");
    Check((int)(await editor.DeleteAsync("/api/admin/recipe-categories/snacks?revision=1")).StatusCode==409,"stale category deletion conflicts");
    Check((int)(await editor.DeleteAsync("/api/admin/recipe-categories/snacks?revision=2")).StatusCode==204,"unused category deletion succeeds");
    Check((int)(await editor.PatchAsJsonAsync("/api/admin/recipes/taxonomy-test/category",new { categoryId="snacks",revision=3 })).StatusCode==400,"removed category cannot be assigned");

    Check((int)(await editor.PutAsJsonAsync("/api/admin/recipes/api-test", new { document = new { }, revision = 0, publish = false })).StatusCode == 400, "API validates malformed document");
    var apiDocument = JsonNode.Parse(seeds[0].GetRawText())!;
    apiDocument["recipe"]!["slug"] = "api-test";
    apiDocument["recipe"]!["title"] = "Published API test";
    Check((await editor.PutAsJsonAsync("/api/admin/recipes/api-test", new { document = apiDocument, revision = 0, publish = false })).IsSuccessStatusCode, "API saves draft");
    Check(!(await guest.GetStringAsync("/api/recipe-catalog")).Contains("api-test"), "public API never leaks draft");
    Check((await editor.PutAsJsonAsync("/api/admin/recipes/api-test", new { document = apiDocument, revision = 1, publish = true })).IsSuccessStatusCode, "API publishes recipe");
    Check((int)(await editor.PutAsJsonAsync("/api/admin/recipes/api-test", new { document = apiDocument, revision = 1, publish = true })).StatusCode == 409, "API forwards revision conflict");
    apiDocument["recipe"]!["title"] = "Unpublished change";
    Check((await editor.PutAsJsonAsync("/api/admin/recipes/api-test", new { document = apiDocument, revision = 2, publish = false })).IsSuccessStatusCode, "API updates draft independently");
    var publicCatalog = await guest.GetStringAsync("/api/recipe-catalog");
    Check(publicCatalog.Contains("Published API test") && !publicCatalog.Contains("Unpublished change"), "public API retains published version");
    Check((int)(await guest.PatchAsJsonAsync("/api/admin/recipes/api-test/category",new { categoryId="soups",revision=3 })).StatusCode==401,"guest cannot change category");
    Check((int)(await editor.PatchAsJsonAsync("/api/admin/recipes/api-test/category",new { categoryId="invalid",revision=3 })).StatusCode==400,"category validates known taxonomy");
    Check((int)(await editor.PatchAsJsonAsync("/api/admin/recipes/api-test/category",new { categoryId="soups",revision=2 })).StatusCode==409,"category respects revision conflicts");
    Check((await editor.PatchAsJsonAsync("/api/admin/recipes/api-test/category",new { categoryId="soups",revision=3 })).IsSuccessStatusCode,"editor changes category independently");
    using(var changed=JsonDocument.Parse(await guest.GetStringAsync("/api/recipe-catalog/api-test"))) {
      var r=changed.RootElement.GetProperty("document").GetProperty("recipe");
      Check(r.GetProperty("categoryId").GetString()=="soups","category applied to public version");
      Check(r.GetProperty("title").GetString()=="Published API test","category edit does not publish draft title");
    }
    var classificationDoc=JsonNode.Parse(seeds[0].GetRawText())!;
    classificationDoc["recipe"]!["slug"]="classification-test";
    classificationDoc["recipe"]!["title"]="Published classification title";
    classificationDoc["recipe"]!["tags"]=new JsonArray("custom-public","easy");
    var publicCategory=classificationDoc["recipe"]!["categoryId"]!.GetValue<string>();
    Check((await editor.PutAsJsonAsync("/api/admin/recipes/classification-test",new { document=classificationDoc,revision=0,publish=true })).IsSuccessStatusCode,"classification creates public fixture");
    classificationDoc["recipe"]!["title"]="Unpublished classification title";
    classificationDoc["recipe"]!["categoryId"]="salads";
    classificationDoc["recipe"]!["tags"]=new JsonArray("custom-draft","quick20");
    Check((await editor.PutAsJsonAsync("/api/admin/recipes/classification-test",new { document=classificationDoc,revision=1,publish=false })).IsSuccessStatusCode,"classification keeps a distinct draft");
    var addFamily=new { revision=2,collections=new { add=new[]{"family"},remove=Array.Empty<string>() } };
    Check((int)(await guest.PatchAsJsonAsync("/api/admin/recipes/classification-test/classification",addFamily)).StatusCode==401,"guest cannot classify recipes");
    Check((int)(await editor.PatchAsJsonAsync("/api/admin/recipes/classification-test/classification",new { revision=2 })).StatusCode==400,"empty classification is rejected");
    Check((int)(await editor.PatchAsJsonAsync("/api/admin/recipes/classification-test/classification",new { revision=2,collections=new {add=new[]{"unknown"},remove=Array.Empty<string>()} })).StatusCode==400,"unknown collection is rejected through API");
    Check((await editor.PatchAsJsonAsync("/api/admin/recipes/classification-test/classification",addFamily)).IsSuccessStatusCode,"collection-only patch succeeds");
    Check((int)(await editor.PatchAsJsonAsync("/api/admin/recipes/classification-test/classification",addFamily)).StatusCode==409,"stale classification patch conflicts");
    using(var result=JsonDocument.Parse(await guest.GetStringAsync("/api/recipe-catalog/classification-test"))) {
      var r=result.RootElement.GetProperty("document").GetProperty("recipe");
      Check(r.GetProperty("categoryId").GetString()==publicCategory,"collection edit never publishes draft category");
      Check(r.GetProperty("title").GetString()=="Published classification title","collection edit never publishes draft text");
      Check(r.GetProperty("tags").EnumerateArray().Select(t=>t.GetString()).SequenceEqual(new[]{"custom-public","easy","family"}),"published tags preserve unrelated published flags");
    }
    var classificationDraft=(await store.Draft("classification-test",default)).GetProperty("recipe");
    Check(classificationDraft.GetProperty("tags").EnumerateArray().Select(t=>t.GetString()).SequenceEqual(new[]{"custom-draft","quick20","family"}),"draft tags preserve unrelated draft flags");
    Check((await editor.PatchAsJsonAsync("/api/admin/recipes/classification-test/classification",new { revision=3,categoryId="soups",collections=new { add=new[]{"light"},remove=new[]{"family"} } })).IsSuccessStatusCode,"category and individual flags save atomically");
    using(var result=JsonDocument.Parse(await guest.GetStringAsync("/api/recipe-catalog/classification-test"))) {
      var r=result.RootElement.GetProperty("document").GetProperty("recipe");
      Check(r.GetProperty("categoryId").GetString()=="soups","classification applies explicitly chosen public category");
      Check(r.GetProperty("tags").EnumerateArray().Select(t=>t.GetString()).SequenceEqual(new[]{"custom-public","easy","light"}),"only requested public flag changes are applied");
    }
    using(var familyPage=JsonDocument.Parse(await guest.GetStringAsync("/api/recipe-catalog/page?collection=family&q=Published%20classification%20title")))
      Check(familyPage.RootElement.GetProperty("documents").GetArrayLength()==0,"unchecking family removes the dish from the collection regardless of servings");
    Check((await editor.DeleteAsync("/api/admin/recipes/classification-test?revision=4")).IsSuccessStatusCode,"classification fixture removed");
    Check((int)(await guest.DeleteAsync("/api/admin/recipes/api-test?revision=3")).StatusCode == 401, "guest cannot delete recipes");
    Check((int)(await editor.DeleteAsync("/api/admin/recipes/api-test?revision=0")).StatusCode == 400, "deletion requires current revision");
    Check((int)(await editor.DeleteAsync("/api/admin/recipes/api-test?revision=2")).StatusCode == 409, "stale deletion conflicts");
    Check((int)(await editor.DeleteAsync("/api/admin/recipes/api-test?revision=4")).StatusCode == 204, "editor deletes recipe");
    Check(!(await guest.GetStringAsync("/api/recipe-catalog")).Contains("api-test"), "deleted recipe disappears from public catalog");
    await using (var revoke = source.CreateCommand("DELETE FROM nabo_recipes.recipe_editors WHERE user_id=@id"))
    {
        revoke.Parameters.AddWithValue("id", id); await revoke.ExecuteNonQueryAsync();
    }
    Check((int)(await editor.GetAsync("/api/admin/recipes/access")).StatusCode == 403, "revoking editor takes immediate effect");

}
else Console.WriteLine("SKIP PostgreSQL integration: TEST_DATABASE_URL is not set.");

class FakeClients(HttpStatusCode status, string profile) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new FakeResponse(status, profile)) { BaseAddress = new Uri("https://nabo.test/") };
}
class FakeResponse(HttpStatusCode status, string profile) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsolutePath != "/api/auth/me" || request.Headers.Authorization?.Scheme != "Bearer")
            throw new Exception("Wrong remote auth contract");
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(profile) });
    }
}
