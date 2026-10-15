# Vulnerable shop (sample)

A tiny API with flaws planted on purpose, used to check Monday's threat rules and to demo the
Threats lens. It is never built or deployed: the solution does not include it, and the values in
it are fake. Index it with:

```bash
dotnet run --project src/Monday.Indexer -- --root samples/vulnerable-shop --name "Demo shop" --out viewer/artifact/demo-shop.json
```
