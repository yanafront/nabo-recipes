# Recipe classification table

The frontend `/admin/recipe-classification` lists every recipe with checkbox columns.
A recipe has one `categoryId`. The table edits the editorial `family` and `budget` flags.
Time, nutrition and difficulty collections continue to follow recipe data automatically. Category definitions are
still managed through `/admin/categories`.

`PATCH /api/admin/recipes/{slug}/classification` requires an editor token and the
current `revision`. Only changed fields should be sent:

```json
{
  "revision": 3,
  "categoryId": "salads",
  "collections": { "add": ["family"], "remove": ["easy"] }
}
```

`categoryId` is optional. `collections` is optional; its `add` and `remove` arrays
must be disjoint and contain only quick20, quick30, budget, protein, light, family,
easy. At least one change is required. The response contains the new revision.

Changes apply independently to draft and published documents. The endpoint never
copies draft text, category or unrelated tags into the published version. Unchanged
flags and custom tags stay intact. A draft-only recipe remains unpublished. Stale
revisions return 409. No database migration is required. The old category-only
endpoint remains compatible.
