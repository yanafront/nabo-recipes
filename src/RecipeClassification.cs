namespace Nabo.Recipes;

public sealed record RecipeCollectionChanges(string[] Add, string[] Remove);
public static class RecipeClassification
{
    public static readonly string[] CollectionIds = ["quick20", "quick30", "budget", "protein", "light", "family", "easy"];
    public static bool ValidCollections(RecipeCollectionChanges? changes) => changes is null ||
        changes.Add is not null && changes.Remove is not null &&
        changes.Add.Length + changes.Remove.Length <= CollectionIds.Length &&
        changes.Add.Concat(changes.Remove).Distinct().Count() == changes.Add.Length + changes.Remove.Length &&
        changes.Add.Concat(changes.Remove).All(id => CollectionIds.Contains(id));
}
