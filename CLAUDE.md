# BIM Monkey — RevitMCPBridge2026

## Build & Deploy
```bash
cd C:\Users\echra\CascadeProjects\RevitMCPBridge2026
dotnet publish -c Release --no-self-contained
# Then copy bin\Release\publish\*.dll to %APPDATA%\Autodesk\Revit\Addins\2026\
# Revit must be fully closed before copying
```

## BIM Monkey API
```
RAILWAY_API_URL=https://bimmonkey-production.up.railway.app
BIM_MONKEY_API_KEY=bm_...   # firm-specific key in CLAUDE.md in Documents\BimMonkey\
```

## Key Facts
- 705 MCP endpoints via named pipe `\\.\pipe\RevitMCPBridge2026`
- C# .NET Framework 4.8, Revit 2026 add-in
- All document modifications must be in Transaction blocks
- Restart Revit after deploying new DLL

## Revit API Notes
- `ScheduleSheetInstance.Create()` for schedules — NOT `Viewport.Create()`
- Empty views cannot be placed on sheets — they must have content first
- `ElementId` always: `new ElementId(int)`
- `ScheduleFieldId` not `int` for field references (Revit 2026)
- Cloud families require manual UI interaction — PostCommand can't pass parameters
- MCP times out if Revit has a modal dialog open

## Method Pattern
```csharp
[MCPMethod("methodName", Category = "Category", Description = "...")]
public static string MethodName(UIApplication uiApp, JObject parameters)
{
    try
    {
        var doc = uiApp.ActiveUIDocument.Document;
        // validate params, then:
        using (var trans = new Transaction(doc, "Operation"))
        {
            trans.Start();
            // ... Revit API calls ...
            trans.Commit();
            return ResponseBuilder.Success().With("key", value).Build();
        }
    }
    catch (Exception ex)
    {
        return ResponseBuilder.FromException(ex).Build();
    }
}
```

## Sheet Methods: When to Use What
- `placeViewOnSheet` — floor plans, elevations, sections, drafting views
- `placeScheduleOnSheet` — Door/Window/Wall schedules (uses ScheduleSheetInstance.Create)
- `createDraftingView` then `placeViewOnSheet` — detail views

## Knowledge Files (on-demand — read only when needed)
Available in `knowledge/` directory. Read specific files as tasks require:
- `revit-api-lessons.md` — API gotchas and patterns
- `cd-standards.md` — sheet organization, numbering
- `annotation-standards.md` — text styles, leaders
- `error-recovery.md` — MCP errors, timeouts
