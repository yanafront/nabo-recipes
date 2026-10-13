using System.Text.Json;
using System.Text.RegularExpressions;
namespace Nabo.Recipes;

public static class RecipeValidation
{
    public static bool ValidCategory(string? category) => CategoryStore.ValidId(category);
    static bool Text(JsonElement e, string key, int max, bool required = true) =>
        e.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= max && (!required || !string.IsNullOrWhiteSpace(v.GetString())) : !required;
    static bool Number(JsonElement e, string key, double min, double max) => e.TryGetProperty(key, out var v) && v.TryGetDouble(out var n) && double.IsFinite(n) && n >= min && n <= max;
    public static bool Valid(string slug, JsonElement d)
    {
        try
        {
            if (!Regex.IsMatch(slug, "^[a-z0-9]+(?:-[a-z0-9]+)*$") || slug.Length > 100) return false;
            var r = d.GetProperty("recipe"); var dictionary = d.GetProperty("ingredients");
            if (r.GetProperty("slug").GetString() != slug || !Text(r, "id", 160) || !Text(r, "title", 200) || !Number(r, "servings", 1, 30)) return false;
            if (!Text(r, "description", 5000, false) || !Text(r, "source", 200) || !Text(r, "sourceId", 200)) return false;
            if (!ValidCategory(r.GetProperty("categoryId").GetString())) return false;
            if (r.GetProperty("tags").GetArrayLength() > 30 || r.GetProperty("tags").EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(t.GetString()) || t.GetString()!.Length > 100)) return false;
            if (r.TryGetProperty("yieldGrams", out _) && !Number(r, "yieldGrams", 1, 100000)) return false;
            if (r.TryGetProperty("cookingTime", out _) && !Number(r, "cookingTime", 0, 10080)) return false;
            if (r.GetProperty("servings").GetDouble() % 1 != 0) return false;
            if (r.GetProperty("isActive").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            if (r.TryGetProperty("image", out var image) && !string.IsNullOrEmpty(image.GetString()) && (!Uri.TryCreate(image.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != "https")) return false;
            if (r.TryGetProperty("sourceUrl", out var sourceUrl) && !string.IsNullOrEmpty(sourceUrl.GetString()) && (!Uri.TryCreate(sourceUrl.GetString(), UriKind.Absolute, out var sourceUri) || sourceUri.Scheme != "https")) return false;
            var rows = r.GetProperty("ingredients");
            if (rows.GetArrayLength() is < 1 or > 20 || dictionary.GetArrayLength() is < 1 or > 20) return false;
            var ids = new HashSet<string>();
            foreach (var i in dictionary.EnumerateArray())
            {
                if (!Text(i, "id", 160) || !Text(i, "name", 200) || !ids.Add(i.GetProperty("id").GetString()!)) return false;
                if (i.GetProperty("pantry").ValueKind is not (JsonValueKind.True or JsonValueKind.False) || i.GetProperty("common").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                if (i.GetProperty("aliases").GetArrayLength() > 30 || i.GetProperty("aliases").EnumerateArray().Any(a => a.ValueKind != JsonValueKind.String)) return false;
                var terms = i.GetProperty("searchTerms");
                if (terms.GetArrayLength() is < 1 or > 10 || terms.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(t.GetString()) || t.GetString()!.Length > 160)) return false;
            }
            var units = new[] { "g", "kg", "ml", "l", "piece", "clove", "tbsp", "tsp", "cup", "pinch", "toTaste", "slice", "sprig", "bunch" };
            foreach (var i in rows.EnumerateArray())
            {
                foreach (var key in new[] { "optional", "unquantified", "scalable" })
                    if (i.TryGetProperty(key, out var flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                if (!Text(i, "name", 200) || !ids.Contains(i.GetProperty("ingredientId").GetString()!) || !Number(i, "quantity", 0, 100000) || !units.Contains(i.GetProperty("unit").GetString())) return false;
                if (i.GetProperty("quantity").GetDouble() == 0 && (!i.TryGetProperty("unquantified", out var u) || u.ValueKind != JsonValueKind.True)) return false;
                if (i.TryGetProperty("weightGrams", out _) && !Number(i, "weightGrams", 0.001, 100000)) return false;
                if (i.TryGetProperty("searchQuery", out var q) && (!Text(i, "searchQuery", 160))) return false;
                if (i.TryGetProperty("exactName", out var n) && !Text(i, "exactName", 500, false)) return false;
                if (i.TryGetProperty("exactUnit", out _) && !Text(i, "exactUnit", 80, false)) return false;
            }
            if (r.TryGetProperty("nutrition", out var nutrition))
            {
                if (!Text(nutrition, "source", 500) || !new[] { "source", "calculated" }.Contains(nutrition.GetProperty("calculationType").GetString())) return false;
                if (!new[] { "serving", "recipe", "100g" }.Contains(nutrition.GetProperty("basis").GetString())) return false;
                foreach (var k in new[] { "calories", "protein", "fat", "carbs" }) if (!Number(nutrition, k, 0, 100000)) return false;
            }
            if (r.TryGetProperty("instructions", out var steps) && (steps.GetArrayLength() > 100 || steps.EnumerateArray().Any(s => !Text(s, "text", 5000)))) return false;
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or FormatException) { return false; }
    }
}
