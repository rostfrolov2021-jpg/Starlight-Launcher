using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Starlight.Domain;

namespace Starlight.Server;

public static partial class ClientApi
{
    [GeneratedRegex("^mods/(search|[0-9]+/files(/[0-9]+)?)$")] private static partial Regex CurseRoute();
    public static void MapClientApi(this WebApplication app)
    {
        app.MapGet("/", () => Results.Content("""
            <!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
            <title>Starlight Service</title><style>body{font:16px system-ui;background:#17191c;color:#eee;max-width:760px;margin:70px auto;padding:24px}a{color:#b5c2ff}code{background:#292c31;padding:4px}p{line-height:1.7}</style>
            <h1>Starlight Service</h1><p>Сервис работает. Скопируйте адрес из строки браузера в Starlight → Настройки → Привязать сервис.</p>
            <p><a href="/health">Проверить состояние</a> · <a href="/api/client-config">Настройки подключения</a> · <a href="/api/updates/latest">Лента обновлений</a></p>
            <p>Microsoft: владелец сервиса задаёт <code>STARLIGHT_MICROSOFT_CLIENT_ID</code>.<br>CurseForge: <code>STARLIGHT_CURSEFORGE_KEY</code> хранится только на сервере.<br>Обновления: каталог <code>STARLIGHT_RELEASES</code>.</p></html>
            """, "text/html; charset=utf-8"));
        app.MapGet("/api/client-config", (IConfiguration config) => Results.Ok(new ServiceInfo("Starlight", typeof(ServiceInfo).Assembly.GetName().Version!.ToString(3), config["STARLIGHT_MICROSOFT_CLIENT_ID"],
            int.TryParse(config["STARLIGHT_NAT_PORT"], out var port) ? port : 5089, "api/updates/latest", !string.IsNullOrWhiteSpace(config["STARLIGHT_CURSEFORGE_KEY"]))));
        app.MapGet("/api/updates/latest", (IConfiguration config) => {
            var file = Path.Combine(ReleaseDirectory(config), "latest.json");
            return File.Exists(file) ? Results.File(file, "application/json") : Results.NotFound(new { error = "Выпуски ещё не опубликованы." });
        });
        app.MapGet("/api/updates/files/{file}", (string file, IConfiguration config) => {
            if (!Regex.IsMatch(file, "^Starlight-[0-9]+\\.[0-9]+\\.[0-9]+-[a-z0-9-]+\\.zip$")) return Results.NotFound();
            var path = Path.Combine(ReleaseDirectory(config), file);
            return File.Exists(path) ? Results.File(path, "application/zip", enableRangeProcessing: true) : Results.NotFound();
        });
        app.MapGet("/api/curseforge/{**path}", async (string path, HttpContext context, IConfiguration config, IHttpClientFactory factory, IMemoryCache cache, CancellationToken ct) => {
            if (!CurseRoute().IsMatch(path)) return Results.NotFound();
            var key = config["STARLIGHT_CURSEFORGE_KEY"];
            if (string.IsNullOrWhiteSpace(key)) return Results.Json(new { error = "Владелец сервиса ещё не настроил CurseForge API key." }, statusCode: 503);
            var allowed = new HashSet<string> { "gameId", "classId", "pageSize", "index", "searchFilter", "sortField", "sortOrder", "gameVersion", "modLoaderType" };
            if (context.Request.Query.Any(q => !allowed.Contains(q.Key) || q.Value.ToString().Length > 150)) return Results.BadRequest();
            var query = context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString());
            query["pageSize"] = Math.Clamp(int.TryParse(query.GetValueOrDefault("pageSize"), out var size) ? size : 25, 1, 100).ToString();
            if (path == "mods/search") query["gameId"] = "432";
            var url = "https://api.curseforge.com/v1/" + path + "?" + string.Join("&", query.Select(q => Uri.EscapeDataString(q.Key) + "=" + Uri.EscapeDataString(q.Value)));
            if (cache.TryGetValue<string>(url, out var cached)) return Results.Content(cached!, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.Add("x-api-key", key);
            using var response = await factory.CreateClient("catalog").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return Results.Json(new { error = "CurseForge вернул ошибку.", status = (int)response.StatusCode }, statusCode: (int)response.StatusCode);
            var text = await response.Content.ReadAsStringAsync(ct); cache.Set(url, text, TimeSpan.FromMinutes(2)); return Results.Content(text, "application/json");
        }).RequireRateLimiting("catalog");
    }
    private static string ReleaseDirectory(IConfiguration config) => Path.GetFullPath(config["STARLIGHT_RELEASES"] ?? Path.Combine(AppContext.BaseDirectory, "releases"));
}
